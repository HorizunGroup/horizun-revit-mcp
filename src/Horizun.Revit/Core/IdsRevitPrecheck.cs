// ASSEMBLY CODE MOVED. BuiltInParameter.UNIFORMAT_CODE and UNIFORMAT_DESCRIPTION
// exist in Revit 2022-2025 and are GONE in 2026 and 2027 - read in RevitAPI.xml
// for all five years, not discovered in a build log. Referencing the enum member
// unconditionally would fail to COMPILE for two of the five cohorts.
#if REVIT2022 || REVIT2023 || REVIT2024 || REVIT2025
#define HORIZUN_HAS_UNIFORMAT_PARAMETER
#endif
// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// THE PRE-CHECK: an IDS read against the LIVE REVIT MODEL, before any export.
//
// IT IS USEFUL AND IT IS NOT VALIDATION, and the difference is the whole reason
// this is a separate file from IdsRun:
//
//   A Revit parameter named "FireRating" is NOT evidence that the exported IFC
//   will carry a property "FireRating" in the property set "Pset_WallCommon".
//   What ends up in which property set is decided by the export mapping, at
//   export time, by settings this code cannot see. A pre-check that reported
//   "Pset_WallCommon/FireRating: pass" would be handing somebody a passing report
//   about a file that does not exist yet.
//
// So every property finding here says, in the finding itself, that PROPERTY SET
// MEMBERSHIP WAS NOT ESTABLISHED. The parameter was found; the set was not
// checked, because it cannot be. That is a smaller claim and it is a true one.
//
// WHAT THE PRE-CHECK IS FOR. Catching the expensive things early: a parameter
// that is not there at all, a value out of range, a naming convention broken.
// Those are all real, all fixable before an export, and all confirmable here.
// It is a screening pass, and it says so in every reply.
//
// WHAT IT ADDS THAT VALIDATION CANNOT. Revit ELEMENT IDS. A finding from an IFC
// names a GlobalId, and the element that needs changing is in the model; mapping
// one to the other needs provenance nobody may have recorded. A finding from here
// names the element directly, so the correction it proposes is a request somebody
// can send.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class IdsRevitPrecheck
    {
        /// <summary>
        /// IFC class → the Revit categories that produce it. CLOSED, and a class that is not
        /// here makes its specification NOT DECIDABLE rather than matching nothing quietly:
        /// a specification that silently applies to zero elements passes.
        /// </summary>
        public static readonly Dictionary<string, BuiltInCategory[]> EntityMap =
            new Dictionary<string, BuiltInCategory[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["IFCWALL"] = new[] { BuiltInCategory.OST_Walls },
                ["IFCWALLSTANDARDCASE"] = new[] { BuiltInCategory.OST_Walls },
                ["IFCSLAB"] = new[] { BuiltInCategory.OST_Floors },
                ["IFCROOF"] = new[] { BuiltInCategory.OST_Roofs },
                ["IFCCOLUMN"] = new[] { BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns },
                ["IFCBEAM"] = new[] { BuiltInCategory.OST_StructuralFraming },
                ["IFCDOOR"] = new[] { BuiltInCategory.OST_Doors },
                ["IFCWINDOW"] = new[] { BuiltInCategory.OST_Windows },
                ["IFCSPACE"] = new[] { BuiltInCategory.OST_Rooms },
                ["IFCSTAIR"] = new[] { BuiltInCategory.OST_Stairs },
                ["IFCRAILING"] = new[] { BuiltInCategory.OST_StairsRailing },
                ["IFCRAMP"] = new[] { BuiltInCategory.OST_Ramps },
                ["IFCCURTAINWALL"] = new[] { BuiltInCategory.OST_Walls },
                ["IFCCOVERING"] = new[] { BuiltInCategory.OST_Ceilings },
                ["IFCFURNISHINGELEMENT"] = new[] { BuiltInCategory.OST_Furniture },
                ["IFCFOOTING"] = new[] { BuiltInCategory.OST_StructuralFoundation },
                ["IFCPILE"] = new[] { BuiltInCategory.OST_StructuralFoundation },
                ["IFCFLOWSEGMENT"] = new[] { BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_DuctCurves },
                ["IFCFLOWFITTING"] = new[] { BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_DuctFitting },
                ["IFCFLOWTERMINAL"] = new[] { BuiltInCategory.OST_PlumbingFixtures,
                                              BuiltInCategory.OST_MechanicalEquipment },
                ["IFCBUILDINGELEMENTPROXY"] = new[] { BuiltInCategory.OST_GenericModel },
                ["IFCMEMBER"] = new[] { BuiltInCategory.OST_StructuralFraming },
                ["IFCPLATE"] = new[] { BuiltInCategory.OST_CurtainWallPanels },
                ["IFCBUILDINGSTOREY"] = new[] { BuiltInCategory.OST_Levels }
            };

        /// <summary>The IFC attributes a Revit element can answer for, and how.</summary>
        private static readonly string[] AnswerableAttributes = { "Name", "Description", "ObjectType", "Tag" };

        public static IdsReport Precheck(Document doc, IdsFile ids, string sourcePath,
                                         CooperativeRead.Scope scope)
        {
            var report = new IdsReport
            {
                Operation = "precheck",
                Evidence = IdsEvidence.RevitPrecheck,
                Source = sourcePath
            };
            report.FileProblems.AddRange(ids.Problems);

            foreach (IdsSpecification specification in ids.Specifications)
                report.Results.Add(PrecheckOne(doc, specification, scope));

            return report;
        }

        private static IdsSpecificationResult PrecheckOne(Document doc, IdsSpecification specification,
                                                          CooperativeRead.Scope scope)
        {
            var result = new IdsSpecificationResult
            {
                Name = specification.Name,
                Identifier = specification.Identifier,
                Evidence = IdsEvidence.RevitPrecheck
            };

            // The same order as the IFC validator: a malformed specification is not asked of
            // a model either. The two paths must agree about what `invalid` means, or a
            // pre-check and a validation of the same IDS disagree for a reason that is not
            // about the model at all.
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

            IdsEntityFacet entityFacet = specification.Applicability.Entity;
            if (entityFacet == null || entityFacet.Name == null || !entityFacet.Name.IsSimple)
            {
                result.Undecidable =
                    "a pre-check selects elements by Revit CATEGORY, so it needs a single IFC class " +
                    "named in the applicability's entity facet. This specification " +
                    (entityFacet == null ? "declares no entity facet"
                        : "constrains the entity name with a restriction rather than naming one class") +
                    ". Validate against an exported IFC instead: there the class is in the file.";
                return result;
            }

            BuiltInCategory[] categories;
            if (!EntityMap.TryGetValue(entityFacet.Name.Simple, out categories))
            {
                result.Undecidable =
                    "IFC class '" + entityFacet.Name.Simple + "' is not in this build's class-to-category " +
                    "table, so a pre-check cannot select the elements it is about. Reporting zero " +
                    "applicable elements would make this specification pass by accident.";
                return result;
            }

            // The facets a pre-check cannot select on are reported, not silently ignored: an
            // applicability narrowed by a property would select MORE elements here than it
            // does in the file, and every extra one would be judged against requirements
            // never meant for it.
            var unselectable = specification.Applicability.All()
                .Where(f => !(f is IdsEntityFacet))
                .Select(f => f.Kind).Distinct().ToList();
            if (unselectable.Count > 0)
                result.Unsupported.Add(
                    "the applicability also narrows by " + string.Join(", ", unselectable) +
                    ", which a pre-check cannot evaluate before an export. EVERY element of the " +
                    "mapped categories was examined, so this pre-check is WIDER than the " +
                    "specification: some elements judged here are not the ones the file will apply " +
                    "it to.");

            var collector = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(categories))
                .WhereElementIsNotElementType();
            List<Element> applicable = collector.ToElements().ToList();
            result.Applicable = applicable.Count;

            if (specification.Mode == IdsApplicabilityMode.Required && applicable.Count == 0)
            {
                result.Failing = 1;
                result.Findings.Add(new IdsElementFinding
                {
                    Outcome = IdsOutcome.Fail,
                    Facet = "applicability",
                    Required = "at least one element of " + entityFacet.Name.Simple,
                    Observed = "none in the mapped Revit categories",
                    Reason = "the model holds no element of the mapped categories and the specification " +
                             "requires at least one."
                });
                return result;
            }
            if (specification.Mode == IdsApplicabilityMode.Prohibited)
            {
                result.Failing = applicable.Count > 0 ? applicable.Count : 0;
                foreach (Element element in applicable.Take(500))
                    result.Findings.Add(Finding(element, IdsOutcome.Fail, "applicability",
                        "no element may match this applicability", "it matches",
                        "the specification prohibits these elements; its requirements were not evaluated"));
                return result;
            }
            if (specification.Requirements == null)
            {
                result.Passing = applicable.Count;
                return result;
            }

            var facets = specification.Requirements.All().ToList();
            var failedByFacet = new Dictionary<string, List<Element>>(StringComparer.Ordinal);

            foreach (Element element in applicable)
            {
                if (scope != null && !scope.Continue()) break;

                bool failed = false, undecided = false;
                foreach (IdsFacet facet in facets)
                {
                    string observed;
                    IdsMatch match = Evaluate(doc, element, facet, out observed);
                    string outcome = IdsRun.Decide(facet.Cardinality, match);
                    if (outcome == IdsOutcome.Pass) continue;

                    if (outcome == IdsOutcome.NotDecidable) undecided = true; else failed = true;

                    IdsElementFinding finding = Finding(element, outcome, facet.Kind,
                        DescribeFacet(facet), observed, match.Reason);
                    // THE SENTENCE THAT KEEPS THIS HONEST, on the finding itself rather than in a
                    // footnote somebody scrolls past.
                    if (facet is IdsPropertyFacet)
                        finding.Reason = (finding.Reason ?? "") +
                            " PROPERTY SET MEMBERSHIP WAS NOT ESTABLISHED: this looked for a Revit " +
                            "parameter of that base name, and which property set it ends up in - if " +
                            "any - is decided by the export mapping at export time.";
                    result.Findings.Add(finding);

                    if (outcome == IdsOutcome.Fail)
                    {
                        List<Element> bucket;
                        string key = DescribeFacet(facet);
                        if (!failedByFacet.TryGetValue(key, out bucket)) failedByFacet[key] = bucket = new List<Element>();
                        bucket.Add(element);
                    }
                }

                if (failed) result.Failing++;
                else if (undecided) result.NotDecidable++;
                else result.Passing++;
            }

            if (scope != null && !scope.Complete)
                result.Unsupported.Add(
                    "the pre-check stopped early (" + scope.PartialWarning() + "). The counts above " +
                    "describe what was examined, NOT the model.");

            Propose(specification, facets, failedByFacet, result);
            return result;
        }

        // =====================================================================
        // Facet evaluation, against Revit
        // =====================================================================

        private static IdsMatch Evaluate(Document doc, Element element, IdsFacet facet, out string observed)
        {
            observed = null;

            var entity = facet as IdsEntityFacet;
            if (entity != null)
            {
                // The requirements entity facet repeats the applicability's class, and the
                // element was selected by that class's categories. There is nothing further
                // for a pre-check to check.
                observed = SafeCategory(element);
                return IdsMatch.Yes();
            }

            var attribute = facet as IdsAttributeFacet;
            if (attribute != null) return AttributeValue(doc, element, attribute, out observed);

            var property = facet as IdsPropertyFacet;
            if (property != null) return Property(element, property, out observed);

            var material = facet as IdsMaterialFacet;
            if (material != null) return Material(doc, element, material, out observed);

            var classification = facet as IdsClassificationFacet;
            if (classification != null) return Classification(element, classification, out observed);

            var partOf = facet as IdsPartOfFacet;
            if (partOf != null) return PartOf(doc, element, partOf, out observed);

            return IdsMatch.Unknown("facet kind '" + facet.Kind + "' is not evaluated by the pre-check.");
        }

        private static IdsMatch AttributeValue(Document doc, Element element, IdsAttributeFacet facet,
                                               out string observed)
        {
            observed = null;
            if (facet.Name == null || !facet.Name.IsSimple)
                return IdsMatch.Unknown("this attribute facet constrains the attribute NAME with a " +
                                        "restriction, which a pre-check cannot resolve.");

            string name = facet.Name.Simple;
            if (!AnswerableAttributes.Contains(name, StringComparer.OrdinalIgnoreCase))
                return IdsMatch.Unknown(
                    "attribute '" + name + "' has no settled Revit equivalent before export. A " +
                    "pre-check answers " + string.Join(", ", AnswerableAttributes) +
                    "; anything else is decided by the export mapping.");

            switch (name.ToUpperInvariant())
            {
                case "NAME": observed = SafeName(element); break;
                case "DESCRIPTION": observed = ParameterText(element, BuiltInParameter.ALL_MODEL_DESCRIPTION); break;
                case "OBJECTTYPE": observed = TypeName(doc, element); break;
                case "TAG": observed = ParameterText(element, BuiltInParameter.ALL_MODEL_MARK); break;
            }

            if (facet.Value == null)
                return string.IsNullOrWhiteSpace(observed)
                    ? IdsMatch.Missing("the Revit equivalent of '" + name + "' is empty")
                    : IdsMatch.Yes();
            return IdsRestriction.Satisfies(observed, facet.Value);
        }

        /// <summary>
        /// A parameter of that base name — and NOT a property-set membership claim.
        ///
        /// The property SET part of the requirement is unanswerable here and the finding says
        /// so. What is answerable, and worth catching before somebody exports a 400 MB file:
        /// the parameter is absent, or its value breaks the constraint.
        /// </summary>
        private static IdsMatch Property(Element element, IdsPropertyFacet facet, out string observed)
        {
            observed = null;
            if (facet.BaseName == null || !facet.BaseName.IsSimple)
                return IdsMatch.Unknown(
                    "this property facet constrains the property NAME with a restriction. Enumerating " +
                    "every Revit parameter a pattern could match is possible in principle and would " +
                    "still not establish property-set membership, so the pre-check does not pretend to.");

            string wanted = facet.BaseName.Simple;
            string value = null;
            bool found = false;
            try
            {
                foreach (Parameter parameter in element.GetOrderedParameters())
                {
                    if (parameter == null || parameter.Definition == null) continue;
                    if (!string.Equals(parameter.Definition.Name, wanted, StringComparison.Ordinal)) continue;
                    found = true;
                    value = ValueText(parameter);
                    if (!string.IsNullOrEmpty(value)) break;
                }
            }
            catch { }

            if (!found)
            {
                // The TYPE carries most exported properties, so an instance-only look would
                // report a wall as missing a property its type defines for every instance.
                try
                {
                    Element type = element.Document.GetElement(element.GetTypeId());
                    if (type != null)
                    {
                        Parameter onType = type.LookupParameter(wanted);
                        if (onType != null) { found = true; value = ValueText(onType); }
                    }
                }
                catch { }
            }

            if (!found)
                return IdsMatch.Missing("no Revit parameter named '" + wanted + "' on the element or its type");

            observed = value ?? "(present, empty)";
            if (facet.Value == null)
                return string.IsNullOrWhiteSpace(value)
                    ? IdsMatch.No("the parameter exists and is empty")
                    : IdsMatch.Yes();
            return IdsRestriction.Satisfies(value, facet.Value);
        }

        private static IdsMatch Material(Document doc, Element element, IdsMaterialFacet facet,
                                         out string observed)
        {
            observed = null;
            var names = new List<string>();
            try
            {
                foreach (ElementId id in element.GetMaterialIds(false))
                {
                    var material = doc.GetElement(id) as Material;
                    if (material != null) names.Add(SafeName(material));
                }
            }
            catch (Exception ex)
            {
                return IdsMatch.Unknown("the element's materials could not be read: " + ex.Message);
            }

            if (names.Count == 0)
                return IdsMatch.Missing("this element reports no material at all");
            observed = string.Join(", ", names.Distinct().Take(8));
            if (facet.Value == null) return IdsMatch.Yes();
            foreach (string name in names)
                if (IdsRestriction.Satisfies(name, facet.Value).Satisfied) return IdsMatch.Yes();
            return IdsMatch.No("its material(s) are [" + observed + "] and none satisfies " +
                               facet.Value.Describe());
        }

        /// <summary>
        /// Classification, which Revit has no native concept of.
        ///
        /// The closest things are Assembly Code and Keynote, and BOTH are conventions rather
        /// than classifications: neither carries the SYSTEM the IDS asks about. So the value
        /// can be compared and the system cannot, and this says which half it answered.
        /// </summary>
        private static IdsMatch Classification(Element element, IdsClassificationFacet facet,
                                               out string observed)
        {
            observed = null;
            // THE ASSEMBLY CODE, two ways, because the enum member was removed in Revit 2026.
            // The fallback is a lookup by the parameter's UI NAME, which is weaker and
            // language-dependent - a Spanish Revit answers to "Código de montaje" - and the
            // reply says so rather than reporting "(none)" for a value that is plainly there.
#if HORIZUN_HAS_UNIFORMAT_PARAMETER
            string assembly = ParameterText(element, BuiltInParameter.UNIFORMAT_CODE);
#else
            string assembly = NamedParameterText(element, "Assembly Code");
#endif
            string keynote = ParameterText(element, BuiltInParameter.KEYNOTE_PARAM);
#if HORIZUN_HAS_UNIFORMAT_PARAMETER
            string assemblyNote = assembly ?? "(none)";
#else
            // NOT "(none)". The enum member does not exist in this Revit, so the value was
            // looked up by an English UI name; in a Revit running another language that finds
            // nothing whether or not the code is set.
            string assemblyNote = assembly ?? "(not established: read by UI name on this Revit, " +
                                              "which is language-dependent)";
#endif
            observed = "assembly code: " + assemblyNote + "; keynote: " + (keynote ?? "(none)");

            if (facet.Value == null)
                return assembly == null && keynote == null
                    ? IdsMatch.Missing("neither Assembly Code nor Keynote is set")
                    : IdsMatch.Unknown(
                        "a code is present and the IDS asks for a classification SYSTEM, which Revit " +
                        "does not record: Assembly Code and Keynote are conventions, not systems. The " +
                        "system half is answerable only against an exported IFC, where " +
                        "IfcClassificationReference names it.");

            bool matches = IdsRestriction.Satisfies(assembly, facet.Value).Satisfied ||
                           IdsRestriction.Satisfies(keynote, facet.Value).Satisfied;
            if (!matches)
                return assembly == null && keynote == null
                    ? IdsMatch.Missing("neither Assembly Code nor Keynote is set")
                    : IdsMatch.No("neither the Assembly Code nor the Keynote satisfies " +
                                  facet.Value.Describe());

            return IdsMatch.Unknown(
                "the CODE matches and the SYSTEM could not be checked: Revit records no classification " +
                "system, so this is not a pass. Validate against an exported IFC to settle it.");
        }

        /// <summary>partOf, as far as a Revit model can answer it.</summary>
        private static IdsMatch PartOf(Document doc, Element element, IdsPartOfFacet facet, out string observed)
        {
            observed = null;
            if (facet.Entity == null || facet.Entity.Name == null || !facet.Entity.Name.IsSimple)
                return IdsMatch.Unknown("this partOf facet does not name a single container class.");

            string wanted = facet.Entity.Name.Simple.ToUpperInvariant();
            switch (wanted)
            {
                case "IFCBUILDINGSTOREY":
                {
                    Element level = null;
                    try { level = doc.GetElement(element.LevelId); } catch { }
                    observed = level == null ? "(no level)" : SafeName(level);
                    return level != null
                        ? IdsMatch.Yes()
                        : IdsMatch.Missing("this element is associated with no level");
                }
                case "IFCSPACE":
                {
                    // A ROOM IS NOT A CONTAINER IN REVIT. Which room an element sits in is a
                    // geometric question, answered per phase, and a wrong answer here would
                    // report an element as outside a room it is plainly in.
                    return IdsMatch.Unknown(
                        "Revit does not record which room an element belongs to: it is a geometric " +
                        "question answered per phase, and the export decides how it becomes an " +
                        "IfcRelContainedInSpatialStructure. Validate against the exported IFC.");
                }
                case "IFCGROUP":
                case "IFCSYSTEM":
                case "IFCZONE":
                {
                    bool grouped = false;
                    try { grouped = element.GroupId != ElementId.InvalidElementId; } catch { }
                    observed = grouped ? "in a Revit group" : "(not grouped)";
                    return grouped
                        ? IdsMatch.Unknown(
                            "the element is in a Revit GROUP, and whether that becomes the IFC group, " +
                            "system or zone the IDS asks for is an export decision. Not a pass.")
                        : IdsMatch.Missing("this element is in no Revit group");
                }
                default:
                    return IdsMatch.Unknown(
                        "a pre-check cannot establish 'part of " + wanted + "': the IFC relationship " +
                        "that would answer it is created at export time.");
            }
        }

        // =====================================================================
        // Corrections, WITH Revit ids
        // =====================================================================

        private static void Propose(IdsSpecification specification, List<IdsFacet> facets,
                                    Dictionary<string, List<Element>> failedByFacet,
                                    IdsSpecificationResult result)
        {
            foreach (KeyValuePair<string, List<Element>> group in failedByFacet)
            {
                IdsFacet facet = facets.FirstOrDefault(f => DescribeFacet(f) == group.Key);
                var correction = new IdsCorrection
                {
                    Specification = specification.Name,
                    Facet = facet == null ? "unknown" : facet.Kind,
                    What = "satisfy: " + group.Key,
                    Why = group.Value.Count + " element(s) failed. " +
                          (facet == null || facet.Instructions == null
                              ? (specification.Instructions ?? "The specification gives no instructions.")
                              : facet.Instructions)
                };
                foreach (Element element in group.Value.Take(200))
                    correction.RevitElementIds.Add(Rid.Value(element.Id));

                correction.Request = BuildWriteRequest(facet, correction.RevitElementIds, specification);
                if (correction.Request == null)
                    correction.NotAutomatable = NotAutomatable(facet);
                result.Corrections.Add(correction);
            }
        }

        /// <summary>
        /// A ready horizun_write_params_verified request, for the ONE case where a correction
        /// is unambiguous: a property facet whose required value is a single simple value.
        ///
        /// Anything else is refused with its reason. A correction built from an enumeration
        /// would have to choose which allowed value the author meant, and a validator that
        /// chooses is a validator writing somebody's model on a guess.
        /// </summary>
        private static JObject BuildWriteRequest(IdsFacet facet, List<long> ids, IdsSpecification specification)
        {
            var property = facet as IdsPropertyFacet;
            if (property == null || property.Value == null || !property.Value.IsSimple) return null;
            if (property.BaseName == null || !property.BaseName.IsSimple) return null;
            if (property.Cardinality != IdsCardinality.Required) return null;
            if (ids.Count == 0) return null;

            var writes = new JArray();
            foreach (long id in ids.Take(200))
                writes.Add(new JObject
                {
                    ["target_id"] = id,
                    ["parameter"] = property.BaseName.Simple,
                    ["value"] = property.Value.Simple
                });

            return new JObject
            {
                ["tool"] = "horizun_write_params_verified",
                ["arguments"] = new JObject
                {
                    ["writes"] = writes,
                    // DRY RUN, ALWAYS, in a proposal. A correction that arrived ready to commit
                    // is a correction somebody sends without reading it.
                    ["dry_run"] = true,
                    ["transaction_name"] = "IDS correction: " + (specification.Name ?? "specification")
                },
                ["means"] =
                    "A REQUEST, rehearsed. It names the parameter the specification requires and the " +
                    "single value it requires, on the elements that failed. Nothing here writes: send " +
                    "it, read the rehearsal, and send it again with dry_run false if it is right. It " +
                    "does NOT guarantee the exported IFC will then satisfy the specification - the " +
                    "property set is decided by the export mapping, which is why the validate " +
                    "operation exists."
            };
        }

        private static string NotAutomatable(IdsFacet facet)
        {
            if (facet is IdsPropertyFacet)
                return "the requirement does not name ONE value - it is a range, an enumeration, a " +
                       "pattern, or the facet is optional or prohibited. Building a write would mean " +
                       "choosing which allowed value the author meant, and a validator that chooses " +
                       "is a validator writing somebody's model on a guess.";
            if (facet is IdsMaterialFacet)
                return "material assignment in Revit is a TYPE property or a compound structure layer, " +
                       "not an instance field. Changing it changes every instance of the type, which " +
                       "is a decision rather than a correction.";
            if (facet is IdsClassificationFacet)
                return "Revit records no classification system, so there is nothing to write that " +
                       "would satisfy the requirement rather than merely look like it.";
            if (facet is IdsPartOfFacet)
                return "containment is created by the export mapping, not by a parameter.";
            return "no tool in this bridge covers this correction unambiguously.";
        }

        // =====================================================================

        private static string DescribeFacet(IdsFacet facet)
        {
            var property = facet as IdsPropertyFacet;
            if (property != null)
                return facet.Cardinality.ToString().ToLowerInvariant() + " property " +
                       (property.PropertySet == null ? "?" : property.PropertySet.Describe()) + " / " +
                       (property.BaseName == null ? "?" : property.BaseName.Describe()) +
                       (property.Value == null ? "" : " = " + property.Value.Describe());
            var attribute = facet as IdsAttributeFacet;
            if (attribute != null)
                return facet.Cardinality.ToString().ToLowerInvariant() + " attribute " +
                       (attribute.Name == null ? "?" : attribute.Name.Describe()) +
                       (attribute.Value == null ? "" : " = " + attribute.Value.Describe());
            var material = facet as IdsMaterialFacet;
            if (material != null)
                return facet.Cardinality.ToString().ToLowerInvariant() + " material " +
                       (material.Value == null ? "(any)" : material.Value.Describe());
            var classification = facet as IdsClassificationFacet;
            if (classification != null)
                return facet.Cardinality.ToString().ToLowerInvariant() + " classification " +
                       (classification.System == null ? "?" : classification.System.Describe());
            var partOf = facet as IdsPartOfFacet;
            if (partOf != null)
                return facet.Cardinality.ToString().ToLowerInvariant() + " part of " +
                       (partOf.Entity == null || partOf.Entity.Name == null ? "?" : partOf.Entity.Name.Describe());
            return facet.Cardinality.ToString().ToLowerInvariant() + " " + facet.Kind;
        }

        private static IdsElementFinding Finding(Element element, string outcome, string facet,
                                                 string required, string observed, string reason) =>
            new IdsElementFinding
            {
                Outcome = outcome,
                Facet = facet,
                RevitElementId = Rid.Value(element.Id),
                EntityKey = SafeUniqueId(element),
                IfcClass = SafeCategory(element),
                Name = SafeName(element),
                Required = required,
                Observed = observed,
                Reason = reason
            };

        /// <summary>
        /// A parameter by its UI name. USED ONLY where a BuiltInParameter no longer exists.
        ///
        /// Weaker than the enum and the difference matters: parameter names are localised, so
        /// this finds nothing in a Revit running in another language. Where it finds nothing
        /// the caller reports "not established", never "absent".
        /// </summary>
        private static string NamedParameterText(Element element, string name)
        {
            try
            {
                Parameter parameter = element.LookupParameter(name);
                if (parameter != null) return ValueText(parameter);
                Element type = element.Document.GetElement(element.GetTypeId());
                parameter = type == null ? null : type.LookupParameter(name);
                return parameter == null ? null : ValueText(parameter);
            }
            catch { return null; }
        }

        private static string ParameterText(Element element, BuiltInParameter builtIn)
        {
            try
            {
                Parameter parameter = element.get_Parameter(builtIn);
                return parameter == null ? null : ValueText(parameter);
            }
            catch { return null; }
        }

        private static string ValueText(Parameter p)
        {
            try
            {
                if (p == null || !p.HasValue) return null;
                switch (p.StorageType)
                {
                    case StorageType.String: return p.AsString();
                    case StorageType.Integer: return p.AsInteger()
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
                    case StorageType.Double: return p.AsDouble()
                        .ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    case StorageType.ElementId: return p.AsValueString();
                    default: return null;
                }
            }
            catch { return null; }
        }

        private static string TypeName(Document doc, Element element)
        {
            try
            {
                Element type = doc.GetElement(element.GetTypeId());
                return type == null ? null : SafeName(type);
            }
            catch { return null; }
        }

        private static string SafeName(Element element)
        {
            try { return element == null ? null : element.Name; } catch { return null; }
        }

        private static string SafeUniqueId(Element element)
        {
            try { return element == null ? null : element.UniqueId; } catch { return null; }
        }

        private static string SafeCategory(Element element)
        {
            try { return element == null || element.Category == null ? null : element.Category.Name; }
            catch { return null; }
        }
    }
}
