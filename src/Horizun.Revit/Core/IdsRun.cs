// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// THE IDS RUN: requirements → evaluation → findings → identified elements →
// proposed correction.
//
// The whole flow in one place, over an EXPORTED IFC. The Revit pre-check is a
// different file with a different evidence level, and they are deliberately not
// sharing a code path: the moment they do, somebody reuses a pre-check result to
// answer a validation question.
//
// THE CARDINALITY LOGIC, which is where a plausible implementation goes wrong.
// Three rules from the manual, and they are not symmetrical:
//
//   APPLICABILITY. (1, unbounded) = required: at least one element must match, and
//   all matches must comply. (0, unbounded) = optional: no element need match, any
//   that do must comply. (0, 0) = prohibited: NO element may match, AND THE
//   REQUIREMENTS ARE NOT EVALUATED. That last clause is easy to miss and produces
//   a report full of requirement failures about elements that should not be there
//   at all — the wrong defect, loudly.
//
//   REQUIREMENT FACETS. required: must be present and satisfied. optional: absent
//   is fine, present must satisfy. prohibited: presence IS the failure.
//
//   AND THE DISTINCTION THOSE THREE TURN ON is absent-versus-unsatisfied, which
//   is why IdsMatch carries `Absent` as a field rather than leaving it to be
//   inferred from the wording of a message.
//
// NOT DECIDABLE NEVER BECOMES A PASS. Not at the element level, not at the facet
// level, not at the specification level. A specification with one unevaluated
// element is a specification nobody has checked.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class IdsRun
    {
        /// <summary>How many elements one specification may examine before it stops and says so.</summary>
        public const int MaxElementsPerSpecification = 200000;

        /// <summary>
        /// Validate every specification against a parsed IFC.
        ///
        /// `schemaOf` is the file's own FILE_SCHEMA. A specification whose ifcVersion does not
        /// include it is reported rather than evaluated: an IDS written for IFC4X3 run against
        /// an IFC2X3 file produces findings about a grammar neither party agreed to.
        /// </summary>
        public static IdsReport Validate(IdsFile ids, IfcStepReader.Document ifc, string sourcePath)
        {
            var report = new IdsReport
            {
                Operation = "validate",
                Evidence = IdsEvidence.IfcValidated,
                Source = sourcePath
            };
            report.FileProblems.AddRange(ids.Problems);

            IdsIfcEvaluator.Index index = IdsIfcEvaluator.Build(ifc);
            List<IfcEntity> rooted = RootedEntities(ifc);

            foreach (IdsSpecification specification in ids.Specifications)
                report.Results.Add(ValidateOne(specification, index, rooted, ifc));

            return report;
        }

        private static IdsSpecificationResult ValidateOne(IdsSpecification specification,
                                                          IdsIfcEvaluator.Index index,
                                                          List<IfcEntity> rooted,
                                                          IfcStepReader.Document ifc)
        {
            var result = new IdsSpecificationResult
            {
                Name = specification.Name,
                Identifier = specification.Identifier,
                Evidence = IdsEvidence.IfcValidated
            };

            // INVALID OUTRANKS EVERYTHING, and it is checked first for that reason. A
            // malformed question is not evaluated against a file: whatever came out would
            // be reported as a finding about elements that are not wrong.
            if (specification.InvalidBecause != null)
            {
                result.InvalidBecause = specification.InvalidBecause;
                return result;
            }

            if (specification.Undecidable != null)
            {
                result.Undecidable = specification.Undecidable;
                return result;
            }

            string schemaProblem = SchemaMismatch(specification, ifc);
            if (schemaProblem != null)
            {
                result.Undecidable = schemaProblem;
                return result;
            }

            // ---- applicability ------------------------------------------------------
            var applicable = new List<IfcEntity>();
            foreach (IfcEntity entity in rooted)
            {
                if (applicable.Count >= MaxElementsPerSpecification)
                {
                    result.Undecidable =
                        "more than " + MaxElementsPerSpecification + " elements matched this " +
                        "applicability. A specification that applies to a whole file at that scale is " +
                        "almost certainly missing a facet, and evaluating it would produce a report " +
                        "nobody can read.";
                    return result;
                }
                string why;
                if (Applies(index, entity, specification.Applicability, out why)) applicable.Add(entity);
            }
            result.Applicable = applicable.Count;

            // ---- the prohibited case, which skips requirements entirely --------------
            if (specification.Mode == IdsApplicabilityMode.Prohibited)
            {
                if (applicable.Count == 0)
                {
                    result.Passing = 0;
                    return result;
                }
                foreach (IfcEntity entity in applicable.Take(MaxElementsPerSpecification))
                {
                    result.Failing++;
                    result.Findings.Add(Finding(entity, IdsOutcome.Fail, "applicability",
                        "no element may match this applicability (minOccurs=0, maxOccurs=0)",
                        "it matches", "this element exists and the specification prohibits it"));
                }
                result.Corrections.Add(new IdsCorrection
                {
                    Specification = specification.Name,
                    Facet = "applicability",
                    What = "remove or re-classify the " + applicable.Count + " element(s) that match a " +
                           "prohibited applicability",
                    Why = "the specification says no element may match it. The requirements were NOT " +
                          "evaluated, deliberately: reporting requirement failures about elements that " +
                          "must not exist would name the wrong defect.",
                    NotAutomatable = "deleting model elements to satisfy a specification is a decision " +
                                     "with consequences this validator will not take. The elements are " +
                                     "identified above."
                });
                foreach (IfcEntity entity in applicable.Take(200))
                    result.Corrections[0].GlobalIds.Add(IfcStepReader.Text(entity.At(0)));
                return result;
            }

            // ---- required means at least one must exist ------------------------------
            if (specification.Mode == IdsApplicabilityMode.Required && applicable.Count == 0)
            {
                result.Failing = 1;
                result.Findings.Add(new IdsElementFinding
                {
                    Outcome = IdsOutcome.Fail,
                    Facet = "applicability",
                    Required = "at least one element matching this applicability (minOccurs >= 1)",
                    Observed = "none",
                    Reason = "the file holds no element that matches this applicability, and the " +
                             "specification requires at least one. That is a failure of the MODEL, not " +
                             "of any element: there is nothing to name."
                });
                return result;
            }

            if (specification.Requirements == null)
            {
                // AN APPLICABILITY WITH NO REQUIREMENTS IS A PRESENCE CHECK, and it has just
                // passed by existing. Saying so beats reporting a vacuous pass with no reason.
                result.Passing = applicable.Count;
                return result;
            }

            // ---- requirements, per element -------------------------------------------
            var facets = specification.Requirements.All().ToList();
            foreach (IdsFacet facet in facets)
                if (facet.Unsupported != null) result.Unsupported.Add(facet.Kind + ": " + facet.Unsupported);

            foreach (IfcEntity entity in applicable)
            {
                bool failed = false, undecided = false;
                foreach (IdsFacet facet in facets)
                {
                    string observed;
                    IdsMatch match = Evaluate(index, entity, facet, out observed);
                    string outcome = Decide(facet.Cardinality, match);

                    if (outcome == IdsOutcome.Pass) continue;
                    if (outcome == IdsOutcome.NotDecidable) undecided = true; else failed = true;

                    result.Findings.Add(Finding(entity, outcome, facet.Kind,
                        Describe(facet), observed, Reason(facet.Cardinality, match)));
                }

                if (failed) result.Failing++;
                else if (undecided) result.NotDecidable++;
                else result.Passing++;
            }

            ProposeCorrections(specification, result);
            return result;
        }

        // =====================================================================
        // Applicability
        // =====================================================================

        /// <summary>
        /// Does this entity match EVERY facet of the applicability?
        ///
        /// Applicability facets carry no cardinality: they are a filter, and every one must
        /// hold. An undecidable facet excludes the element rather than including it — an
        /// element swept into a specification by a facet nobody could evaluate would then be
        /// judged against requirements that were never meant for it.
        /// </summary>
        private static bool Applies(IdsIfcEvaluator.Index index, IfcEntity entity,
                                    IdsFacetSet applicability, out string why)
        {
            why = null;
            foreach (IdsFacet facet in applicability.All())
            {
                string observed;
                IdsMatch match = Evaluate(index, entity, facet, out observed);
                if (!match.Satisfied)
                {
                    why = facet.Kind + ": " + match.Reason;
                    return false;
                }
            }
            return true;
        }

        private static IdsMatch Evaluate(IdsIfcEvaluator.Index index, IfcEntity entity, IdsFacet facet,
                                         out string observed)
        {
            observed = null;
            var entityFacet = facet as IdsEntityFacet;
            if (entityFacet != null)
            {
                observed = entity.Type;
                return IdsIfcEvaluator.MatchesEntity(index, entity, entityFacet);
            }

            var attribute = facet as IdsAttributeFacet;
            if (attribute != null) return IdsIfcEvaluator.MatchesAttribute(index, entity, attribute);

            var property = facet as IdsPropertyFacet;
            if (property != null) return IdsIfcEvaluator.MatchesProperty(index, entity, property, out observed);

            var classification = facet as IdsClassificationFacet;
            if (classification != null)
                return IdsIfcEvaluator.MatchesClassification(index, entity, classification, out observed);

            var material = facet as IdsMaterialFacet;
            if (material != null) return IdsIfcEvaluator.MatchesMaterial(index, entity, material, out observed);

            var partOf = facet as IdsPartOfFacet;
            if (partOf != null) return IdsIfcEvaluator.MatchesPartOf(index, entity, partOf, out observed);

            return IdsMatch.Unknown("facet kind '" + facet.Kind + "' is not evaluated by this build.");
        }

        // =====================================================================
        // Cardinality
        // =====================================================================

        /// <summary>
        /// The three cardinality rules, and they are not symmetrical.
        ///
        /// required   — present and satisfied
        /// optional   — absent is fine; present must satisfy
        /// prohibited — presence IS the failure
        /// </summary>
        public static string Decide(IdsCardinality cardinality, IdsMatch match)
        {
            if (match.Undecidable) return IdsOutcome.NotDecidable;

            switch (cardinality)
            {
                case IdsCardinality.Required:
                    return match.Satisfied ? IdsOutcome.Pass : IdsOutcome.Fail;

                case IdsCardinality.Optional:
                    // ABSENT IS FINE. Present and wrong is not.
                    if (match.Satisfied || match.Absent) return IdsOutcome.Pass;
                    return IdsOutcome.Fail;

                case IdsCardinality.Prohibited:
                    // THE ONE THAT INVERTS. A satisfied match is the defect.
                    return match.Satisfied ? IdsOutcome.Fail : IdsOutcome.Pass;

                default:
                    return IdsOutcome.NotDecidable;
            }
        }

        private static string Reason(IdsCardinality cardinality, IdsMatch match)
        {
            if (cardinality == IdsCardinality.Prohibited && match.Satisfied)
                return "this is PROHIBITED and the element has it.";
            if (cardinality == IdsCardinality.Optional && !match.Satisfied && !match.Absent)
                return "this is OPTIONAL, and where it is present it must comply: " + match.Reason;
            return match.Reason;
        }

        private static string Describe(IdsFacet facet)
        {
            string prefix = facet.Cardinality.ToString().ToLowerInvariant() + " ";
            var property = facet as IdsPropertyFacet;
            if (property != null)
                return prefix + "property " + Show(property.PropertySet) + " / " + Show(property.BaseName) +
                       (property.Value == null ? "" : " with value " + property.Value.Describe()) +
                       (property.DataType == null ? "" : " [dataType " + property.DataType + "]");

            var attribute = facet as IdsAttributeFacet;
            if (attribute != null)
                return prefix + "attribute " + Show(attribute.Name) +
                       (attribute.Value == null ? "" : " with value " + attribute.Value.Describe());

            var classification = facet as IdsClassificationFacet;
            if (classification != null)
                return prefix + "classification in system " + Show(classification.System) +
                       (classification.Value == null ? "" : " with reference " + classification.Value.Describe());

            var material = facet as IdsMaterialFacet;
            if (material != null)
                return prefix + "material" + (material.Value == null ? "" : " " + material.Value.Describe());

            var partOf = facet as IdsPartOfFacet;
            if (partOf != null)
                return prefix + "part of " +
                       (partOf.Entity == null || partOf.Entity.Name == null ? "(unnamed)" : partOf.Entity.Name.Describe()) +
                       (partOf.Relation == null ? " by any relation" : " by " + partOf.Relation);

            var entity = facet as IdsEntityFacet;
            if (entity != null)
                return prefix + "entity " + Show(entity.Name) +
                       (entity.PredefinedType == null ? "" : " predefined " + entity.PredefinedType.Describe());

            return prefix + facet.Kind;
        }

        private static string Show(IdsValue value) => value == null ? "(unspecified)" : value.Describe();

        private static IdsElementFinding Finding(IfcEntity entity, string outcome, string facet,
                                                 string required, string observed, string reason) =>
            new IdsElementFinding
            {
                Outcome = outcome,
                Facet = facet,
                EntityKey = "#" + entity.Id,
                GlobalId = IfcStepReader.Text(entity.At(0)),
                IfcClass = entity.Type,
                Name = IfcStepReader.Text(entity.At(2)),
                Required = required,
                Observed = observed,
                Reason = reason
            };

        // =====================================================================
        // Corrections
        // =====================================================================

        /// <summary>
        /// Group failures into proposals somebody could act on. NOTHING IS APPLIED.
        ///
        /// A correction found in an IFC names elements by GlobalId, and the sentence that has
        /// to be there says why that is not immediately actionable: the file is a projection
        /// of a Revit model, and the element that needs changing is in the model. Mapping one
        /// to the other needs the provenance the import recorded or the IfcGUID parameter the
        /// exporter wrote, and a correction that quietly assumed one would edit whatever
        /// element happened to answer.
        /// </summary>
        private static void ProposeCorrections(IdsSpecification specification, IdsSpecificationResult result)
        {
            foreach (var group in result.Findings.Where(f => f.Outcome == IdsOutcome.Fail)
                                                 .GroupBy(f => f.Facet + "|" + f.Required))
            {
                var correction = new IdsCorrection
                {
                    Specification = specification.Name,
                    Facet = group.First().Facet,
                    What = "satisfy: " + group.First().Required,
                    Why = group.Count() + " element(s) failed this requirement. " +
                          (specification.Instructions ?? specification.RequirementsDescription ??
                           "The specification gives no instructions for authors."),
                    NotAutomatable =
                        "these elements are identified by IFC GlobalId, and the change belongs in the " +
                        "REVIT MODEL that produced the file. Mapping one to the other needs the " +
                        "provenance an import recorded or the IfcGUID parameter the exporter wrote; " +
                        "assuming a mapping would edit whichever element happened to answer. Run the " +
                        "pre-check operation against the model to get a request with Revit ids in it."
                };
                foreach (IdsElementFinding finding in group.Take(200))
                    if (finding.GlobalId != null) correction.GlobalIds.Add(finding.GlobalId);
                result.Corrections.Add(correction);
            }
        }

        // =====================================================================

        /// <summary>
        /// Everything in the file that is an IfcRoot: the entities IDS is about.
        ///
        /// Identified by the shape of the first attribute — a 22-character base64 GlobalId —
        /// rather than by a list of class names. A closed list would silently skip every class
        /// this build had not heard of, which for IFC4X3 is most of them.
        /// </summary>
        public static List<IfcEntity> RootedEntities(IfcStepReader.Document ifc)
        {
            var rooted = new List<IfcEntity>();
            if (ifc == null) return rooted;
            foreach (KeyValuePair<int, IfcEntity> pair in ifc.ById)
            {
                string globalId = IfcStepReader.Text(pair.Value.At(0));
                if (globalId != null && globalId.Length == 22) rooted.Add(pair.Value);
            }
            return rooted;
        }

        private static string SchemaMismatch(IdsSpecification specification, IfcStepReader.Document ifc)
        {
            string fileSchema = ifc == null ? null : ifc.SchemaIdentifier;
            if (string.IsNullOrWhiteSpace(fileSchema) || specification.IfcVersions.Count == 0) return null;

            string normalised = fileSchema.Trim().ToUpperInvariant().Replace("-", "_");
            foreach (string declared in specification.IfcVersions)
            {
                string want = declared.Trim().ToUpperInvariant();
                if (normalised.StartsWith(want, StringComparison.Ordinal)) return null;
                // IFC4X3_ADD2 files write FILE_SCHEMA as IFC4X3_ADD2, and some write IFC4X3.
                if (want.StartsWith(normalised, StringComparison.Ordinal)) return null;
            }

            return "this specification declares ifcVersion " + string.Join(", ", specification.IfcVersions) +
                   " and the file declares FILE_SCHEMA '" + fileSchema + "'. It was NOT evaluated: an " +
                   "IDS written for one schema run against another produces findings about a grammar " +
                   "neither party agreed to, and those findings look exactly like real ones.";
        }
    }
}
