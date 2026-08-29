// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_audit_cad_model - does this model still agree with this drawing?
//
// READ-ONLY, and that is the point rather than a limitation. An audit that could
// change what it measures cannot be used as evidence, and "the drawing and the
// model disagree" is a sentence somebody has to act on with judgement: deleting
// an element because a DWG no longer shows it is a decision about somebody's
// deliverable, not a tidy-up.
//
// It reads the drawing exactly as horizun_plan_from_cad does - same harvest,
// same requirement set, same interpretation - so a finding here and a plan there
// are talking about the same entities under the same names.
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
    public sealed class AuditCadModelCommand : ICommand
    {
        public string Name => "horizun_audit_cad_model";

        public string Description =>
            "Compare a CAD drawing against the model built from it, and report where they disagree. Read-only.";

        public CommandResult Execute(UIApplication uiApp, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("The arguments are not valid JSON: " + ex.Message); }

            Document doc = uiApp?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            string wantDoc = request.Value<string>("target_document");
            if (!string.IsNullOrWhiteSpace(wantDoc) &&
                !string.Equals(wantDoc, SafeTitle(doc), StringComparison.Ordinal))
                return CommandResult.Fail(
                    "target_document is '" + wantDoc + "' and the active document is '" + SafeTitle(doc) +
                    "'. An audit of the wrong model is worse than no audit, because it reads as evidence. " +
                    "Nothing was examined.");

            long instanceId = request.Value<long?>("instance_id") ?? -1;
            if (instanceId < 0 || !Rid.CanRepresent(instanceId))
                return CommandResult.Fail(
                    "instance_id is required: which CAD instance is this model supposed to agree with? List them " +
                    "with horizun_query_cad mode='instances'. There is no default drawing.");

            Element element = doc.GetElement(Rid.Make(instanceId));
            if (element == null)
                return CommandResult.Fail("No element " + instanceId + " in '" + SafeTitle(doc) + "'.");

            JObject setJson = request["requirement_set"] as JObject;
            if (setJson == null)
                return CommandResult.Fail(
                    "requirement_set is required. An audit without the rules that built the model would be " +
                    "comparing the drawing against an interpretation nobody declared.");

            CadRequirementSet set;
            try { set = CadRequirementSet.Load(setJson); }
            catch (Exception ex) { return CommandResult.Fail(ex.Message); }

            if (!(element is ImportInstance))
                return CommandResult.Fail("Element " + instanceId + " is a " + element.GetType().Name +
                                          ", not an ImportInstance.");

            List<JObject> unreadable;
            CadInstanceFacts facts = CadFacts.Collect(doc, out unreadable)
                .FirstOrDefault(f => f.ElementId == instanceId);
            if (facts == null)
                return CommandResult.Fail(
                    "CAD instance " + instanceId + " could not be measured; horizun_query_cad lists it as " +
                    "unreadable. Auditing against a drawing nothing could read would report every element in " +
                    "the model as deleted from the DWG.");

            // The unit check that stops a 200 becoming 200 metres applies to an
            // audit too: read at the wrong scale, nothing matches by position and
            // every entity reads as missing.
            string declaredUnits = facts.DeclaredUnits;
            double? declaredToMm = CadUnits.MillimetresPer(declaredUnits);
            if (!declaredToMm.HasValue ||
                Math.Abs(declaredToMm.Value - set.SourceUnitsToMm) > 1e-9)
                return CommandResult.Fail(
                    "unit_mismatch: the CAD link declares '" + (declaredUnits ?? "(nothing)") + "' and the " +
                    "requirement set declares '" + set.SourceUnits + "'. Read at the wrong scale nothing matches " +
                    "by position and every entity reads as missing, which is a false report rather than a " +
                    "finding. Nothing was examined.");

            string sourceFingerprint = CadFacts.SourceFingerprint(facts);
            string sourceHash = facts.FileSha256 ?? sourceFingerprint ?? "(no-source-identity)";

            CadHarvest harvest = CadGeometryHarvest.Harvest(doc, element, set.ArcSagittaMm,
                Math.Max(1, Math.Min(500000, request.Value<int?>("max_primitives") ?? 200000)));

            if (harvest.GeometryUnreadable)
                return CommandResult.Fail(
                    "geometry_unreadable: Revit returned no geometry for CAD instance " + instanceId +
                    ". An audit against a drawing that could not be read would report every element in the model " +
                    "as built_not_in_drawing, which is a false accusation, not a finding. Nothing was examined.");
            if (harvest.Truncated)
                return CommandResult.Fail(
                    "reading_is_partial: the geometry walk stopped at its bound, so part of this drawing was " +
                    "never read - and every entity past the bound would be reported as deleted from the DWG. " +
                    "Raise max_primitives, or audit in layer-filtered passes. Nothing was examined.");

            CadInterpretation interpretation = CadInterpretationRules.Interpret(
                harvest.Segments, set, sourceHash, harvest.Arcs);

            // ------------------------------------------------------- read the model
            var problems = new List<string>();
            List<CadAuditSubject> subjects = Subjects(doc, set, interpretation, problems,
                request.Value<bool?>("include_anonymous") ?? true);

            CadAudit audit = CadAuditRules.Compare(interpretation.Candidates, subjects, set,
                                                   sourceFingerprint, facts.FileSha256);

            // ------------------------------------------------------------- report
            int maxFindings = Math.Max(1, Math.Min(2000, request.Value<int?>("max_findings") ?? 500));
            List<CadFinding> shown = audit.Findings
                .OrderBy(f => SeverityRank(f.Severity))
                .ThenBy(f => f.Code, StringComparer.Ordinal)
                .Take(maxFindings).ToList();

            var result = new JObject
            {
                ["document"] = SafeTitle(doc),
                ["instance_id"] = instanceId,
                ["read_only"] = true,
                ["read_only_means"] = "nothing in this document was changed, and nothing will be. Every finding " +
                                      "below names a decision for a person: an audit that deleted what a drawing " +
                                      "stopped showing would be editing somebody's deliverable on the strength of " +
                                      "a file they may not have issued.",
                ["source"] = new JObject
                {
                    ["fingerprint"] = sourceFingerprint,
                    ["file_sha256"] = facts.FileSha256,
                    ["external_path"] = facts.ExternalPath,
                    ["declared_units"] = facts.DeclaredUnits,
                    ["linked_file_status"] = facts.LinkedFileStatus
                },
                ["requirement_set"] = new JObject
                {
                    ["id"] = set.Id,
                    ["version"] = set.Version,
                    ["sha256"] = set.Sha256
                },
                ["drawing"] = new JObject
                {
                    ["candidates"] = audit.CandidatesRead,
                    ["needing_review"] = interpretation.NeedingReview.Count(),
                    ["harvest_coverage"] = harvest.CoverageJson(set.ArcSagittaMm),
                    ["interpretation_coverage"] = Math.Round(interpretation.CoverageFraction, 4)
                },
                ["model"] = new JObject
                {
                    ["examined"] = audit.SubjectsExamined,
                    ["carrying_provenance"] = subjects.Count(s => s.Provenance != null),
                    ["anonymous"] = subjects.Count(s => s.Provenance == null)
                },
                ["matched"] = new JObject
                {
                    ["total"] = audit.Matches.Count,
                    ["by_revision"] = audit.MatchedOn("revision"),
                    ["by_semantic"] = audit.MatchedOn("semantic"),
                    ["by_geometry"] = audit.MatchedOn("geometry"),
                    ["by_position"] = audit.MatchedOn("position"),
                    ["ladder_means"] = "revision: the same entity in the same issue of the file. semantic: the " +
                                       "same entity in a DIFFERENT issue - the file was re-cut, the building was " +
                                       "not. geometry: the same shape on a different layer, which usually means a " +
                                       "change of meaning. position: no provenance at all, something merely " +
                                       "standing there - the audit counts it as built, and an incremental update " +
                                       "will NOT recognise it."
                },
                ["findings"] = new JArray(shown.Select(f => f.ToJson())),
                ["findings_total"] = audit.Findings.Count,
                ["findings_truncated"] = audit.Findings.Count > shown.Count,
                ["counts_by_code"] = audit.CountsByCode(),
                ["finding_vocabulary"] = new JArray(CadFindingCode.All),
                ["finding_vocabulary_means"] =
                    "Every code this audit can report, with its count - INCLUDING the zeros. A key that simply " +
                    "disappeared would read as 'not measured', and for the codes that matter most that is the " +
                    "opposite of the truth: 'no unhosted doors' and 'hosting was never checked' would be the " +
                    "same absent key.",
                ["counts_by_severity"] = new JObject
                {
                    ["blocking"] = audit.Findings.Count(f => f.Severity == CadAuditRules.Blocking),
                    ["review"] = audit.Findings.Count(f => f.Severity == CadAuditRules.Review),
                    ["informational"] = audit.Findings.Count(f => f.Severity == CadAuditRules.Informational)
                },
                ["agrees"] = audit.Findings.All(f => f.Severity == CadAuditRules.Informational),
                ["agrees_means"] = "true when nothing needs a decision: every entity in the drawing is in the " +
                                   "model and every element built from this drawing is still in the drawing. " +
                                   "Informational findings do not make it false - they record HOW the two agree.",
                ["provenance_problems"] = new JArray(problems.Take(50)),
                ["not_measured"] = new JArray(
                    "text, blocks and hatches: this bridge cannot read them from imported CAD, so nothing in " +
                    "them is audited and their absence from the findings is not evidence of agreement",
                    "elements built from this drawing and then DELETED: nothing remains to carry provenance, so " +
                    "a deleted element and one that was never built are the same finding here")
            };
            return CommandResult.Ok(result);
        }

        /// <summary>
        /// Everything in the model this audit could reasonably be about: every
        /// element carrying Horizun CAD provenance, plus - unless the caller says
        /// otherwise - the elements in the categories this requirement set
        /// produces, so a hand-built wall standing on the drawing's line is seen
        /// rather than reported as missing.
        /// </summary>
        private static List<CadAuditSubject> Subjects(Document doc, CadRequirementSet set,
                                                      CadInterpretation interpretation,
                                                      List<string> problems, bool includeAnonymous)
        {
            var byId = new Dictionary<long, CadAuditSubject>();
            // Only the parameters some rule actually writes are read back. A
            // sweep of every parameter of every element would cost more than the
            // audit and answer a question nobody asked.
            List<string> wanted = WantedParameters(set);

            foreach (Element e in Stamped(doc))
            {
                string problem;
                CadProvenance p = CadProvenanceStore.Read(e, out problem);
                var s = Measure(e, wanted);
                s.ProvenanceProblem = problem;
                s.Provenance = p;
                if (problem != null) problems.Add("element " + Rid.Value(e.Id) + ": " + problem);
                byId[s.ElementId] = s;
            }

            if (includeAnonymous)
            {
                foreach (BuiltInCategory bic in CategoriesOf(set, interpretation))
                {
                    FilteredElementCollector collector;
                    try
                    {
                        collector = new FilteredElementCollector(doc)
                            .OfCategory(bic).WhereElementIsNotElementType();
                    }
                    catch { continue; }

                    foreach (Element e in collector)
                    {
                        long id = Rid.Value(e.Id);
                        if (byId.ContainsKey(id)) continue;
                        byId[id] = Measure(e, wanted);
                    }
                }
            }

            return byId.Values.OrderBy(s => s.ElementId).ToList();
        }

        private static IEnumerable<Element> Stamped(Document doc)
        {
            List<Element> found = new List<Element>();
            try
            {
                if (Schema.Lookup(CadProvenanceStore.SchemaGuid) == null)
                    return found;   // nothing has ever been stamped in this session
                found = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WherePasses(new ExtensibleStorageFilter(CadProvenanceStore.SchemaGuid))
                    .ToList();
            }
            catch { }
            return found;
        }

        /// <summary>The categories this requirement set can produce, so the sweep is bounded by the rules.</summary>
        private static IEnumerable<BuiltInCategory> CategoriesOf(CadRequirementSet set, CadInterpretation interpretation)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CadRule r in set.Rules)
                if (!string.IsNullOrWhiteSpace(r.Category)) names.Add(r.Category);
            foreach (CadCandidate c in interpretation.Candidates)
                if (!string.IsNullOrWhiteSpace(c.Category)) names.Add(c.Category);

            foreach (string n in names)
            {
                BuiltInCategory bic;
                if (Enum.TryParse(n, true, out bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
                    yield return bic;
            }
        }

        /// <summary>
        /// Every parameter name some rule in this set writes. Passed to the
        /// reader so it looks up those and nothing else.
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

        private static CadAuditSubject Measure(Element e, IEnumerable<string> wanted)
            => CadSubjectReader.Measure(e, wanted);


        private static int SeverityRank(string severity)
        {
            if (severity == CadAuditRules.Blocking) return 0;
            if (severity == CadAuditRules.Review) return 1;
            return 2;
        }

        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
    }
}
