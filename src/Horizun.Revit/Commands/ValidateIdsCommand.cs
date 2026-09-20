// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// horizun_validate_ids — buildingSMART IDS, as TWO operations that answer two
// different questions and are never added together.
//
//   operation=precheck   reads the LIVE REVIT MODEL. Fast, available before
//                        anybody exports, and structurally unable to answer part
//                        of what IDS asks. Every property finding says so: a
//                        Revit parameter named "FireRating" is not evidence that
//                        the export will put it in "Pset_WallCommon", because the
//                        export mapping decides that at export time.
//
//   operation=validate   reads an EXPORTED IFC. Slower, needs the export to have
//                        happened, and answers what IDS actually asks - the
//                        property set, the classification, the material
//                        association and the partOf relations are all IN the file
//                        as relationships.
//
// THE PREVIOUS VERSION OF THIS COMMAND had one operation, read the model, and
// supported three of the six facets. It was honest about its limits in prose and
// the limits were larger than the prose admitted: classification, material and
// partOf were simply unwritten, and the cardinality attribute was not read at
// all, so `prohibited` - the one cardinality that fails when something IS present
// - passed every element in every model.
//
// WHAT IS IMPLEMENTED NOW, against Schema/ids.xsd 1.0.0 read 2026-09-15:
//   all six facets · the four restriction kinds, with XML Schema's anchored
//   pattern semantics · required/optional/prohibited per facet · the
//   applicability's own minOccurs/maxOccurs, including the prohibited case that
//   must NOT evaluate requirements · ifcVersion against the file's FILE_SCHEMA ·
//   the namespace as a version handshake · unsupported constructions named per
//   specification rather than skipped.
//
// WHAT IT IS NOT: a certificate. It is what THIS build evaluated, at the evidence
// level it names, and the reply says that in those words.
//
// READ-ONLY. It opens no transaction. Corrections are PROPOSED - as requests
// against tools that already rehearse, confirm and re-read their own work - and
// never applied: a validator that could also edit the model is a validator whose
// findings nobody can trust, because the thing reporting the defect also caused
// the fix.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ValidateIdsCommand : ICommand
    {
        public string Name => "horizun_validate_ids";

        public string Description =>
            "Evaluate a buildingSMART IDS. Two operations: 'precheck' reads the live Revit model and says " +
            "so on every finding; 'validate' reads an EXPORTED IFC and answers what IDS actually asks. " +
            "All six facets, the four restriction kinds, and required/optional/prohibited cardinality. " +
            "Read-only: corrections are proposed as requests, never applied.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            Document doc = app == null || app.ActiveUIDocument == null ? null : app.ActiveUIDocument.Document;

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            string operation = (request.Value<string>("operation") ?? "precheck").ToLowerInvariant();
            if (operation != "precheck" && operation != "validate")
                return CommandResult.Fail(
                    "operation must be 'precheck' (read the live Revit model, before export) or 'validate' " +
                    "(read an exported IFC). They answer different questions and their results are never " +
                    "added together.");

            string path = request.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return CommandResult.Fail("path is required: the .ids file.");

            string readError;
            IdsFile ids = IdsReader.Read(path, out readError);
            if (ids == null) return CommandResult.Fail(readError);
            if (ids.Specifications.Count == 0)
                return CommandResult.Fail(
                    "'" + path + "' parsed and holds no <specification>. An IDS with no specification " +
                    "demands nothing, and reporting a pass would report that the model satisfied an empty " +
                    "demand. File problems: " + string.Join("; ", ids.Problems));

            int maxFindings = request.Value<int?>("max_findings") ?? 100;
            if (maxFindings < 1 || maxFindings > 5000)
                return CommandResult.Fail("max_findings must be 1..5000.");

            return operation == "validate"
                ? Validate(request, ids, path, maxFindings)
                : Precheck(app, doc, request, ids, path, maxFindings);
        }

        // =====================================================================
        // A. The pre-check, over the live model
        // =====================================================================

        private CommandResult Precheck(UIApplication app, Document doc, JObject request, IdsFile ids,
                                       string path, int maxFindings)
        {
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            CommandResult wrongDocument = DocumentGate.ReadGuard(doc, request, Name);
            if (wrongDocument != null) return wrongDocument;

            // THE LONG LOOP, AND THE ONLY ONE WORTH MAKING INTERRUPTIBLE. A specification
            // applicable to 80,000 elements holds Revit's UI thread for as long as it takes.
            // Opt-in, like every other reader: absent means today's behaviour.
            string coopFingerprint = CooperativeOptions.FingerprintOf(request, Name, doc);
            CooperativeOptions cooperative = CooperativeOptions.Read(request, coopFingerprint);
            if (cooperative.Refusal != null)
                return CommandResult.Fail("cooperative: " + cooperative.Refusal);
            CooperativeRead.Scope scope = cooperative.Begin("ids precheck", 0);

            IdsReport report = IdsRevitPrecheck.Precheck(doc, ids, path, scope);
            JObject payload = report.ToJson(maxFindings);
            payload["document"] = SafeTitle(doc);
            payload["ids"] = ids.InfoJson();

            JObject coopReport = cooperative.Report(scope, 0, coopFingerprint,
                                                    scope != null && !scope.Complete);
            if (coopReport != null) payload["cooperative"] = coopReport;

            payload["what_a_precheck_cannot_answer"] =
                "PROPERTY SET MEMBERSHIP. Which Pset a parameter lands in is decided by the IFC export " +
                "mapping at export time, so this looked for a parameter of that base NAME on the element " +
                "and on its type. CLASSIFICATION SYSTEM: Revit records none - Assembly Code and Keynote " +
                "are conventions, not systems. CONTAINMENT beyond the level: the IFC relationship that " +
                "would answer it is created at export. Each of those is reported as not_decidable per " +
                "element, never as a pass.";
            payload["how_to_settle_it"] =
                "Export the IFC and call this again with operation='validate' and ifc_path. There the " +
                "property set is an IfcPropertySet attached by an IfcRelDefinesByProperties, and it is " +
                "either there or it is not.";
            return CommandResult.Ok(payload);
        }

        // =====================================================================
        // B. The validation, over the exported IFC
        // =====================================================================

        private CommandResult Validate(JObject request, IdsFile ids, string path, int maxFindings)
        {
            string ifcPath = request.Value<string>("ifc_path");
            if (string.IsNullOrWhiteSpace(ifcPath))
                return CommandResult.Fail(
                    "ifc_path is required for operation='validate': this operation reads the EXPORTED " +
                    "IFC, which is what IDS is written against. Without a file there is nothing to " +
                    "validate, and falling back to the Revit model would answer a different question " +
                    "under this operation's name.");

            string ifcError;
            IfcStepReader.Document ifc = IfcStepReader.Read(ifcPath, out ifcError);
            if (ifc == null) return CommandResult.Fail(ifcError);

            IdsReport report = IdsRun.Validate(ids, ifc, path);
            JObject payload = report.ToJson(maxFindings);
            payload["ifc_path"] = ifcPath;
            payload["ifc_schema"] = ifc.SchemaIdentifier;
            payload["ifc_entity_instances"] = ifc.ById.Count;
            payload["ids"] = ids.InfoJson();

            payload["what_this_build_evaluates"] = new JObject
            {
                ["facets"] = new JArray("entity", "attribute", "classification", "property",
                                        "material", "partOf"),
                ["restrictions"] = new JArray("enumeration", "pattern (anchored, whole value)",
                                              "bounds (min/maxInclusive, min/maxExclusive)",
                                              "length / minLength / maxLength"),
                ["cardinality"] = new JArray("required", "optional", "prohibited",
                                             "applicability minOccurs/maxOccurs"),
                ["attributes_resolvable_by_name"] = new JArray("GlobalId", "Name", "Description", "ObjectType"),
                ["attributes_note"] =
                    "Resolving an arbitrary IFC attribute by NAME needs the EXPRESS schema, which this " +
                    "build does not carry. Only the positionally fixed attributes of IfcRoot and " +
                    "IfcObject are answered; anything else is not_decidable, never a pass.",
                ["property_sets"] =
                    "Read from IfcRelDefinesByProperties AND from the type through IfcRelDefinesByType. " +
                    "An occurrence inherits its type's sets, and a validator that read only the " +
                    "occurrence would report 'no Pset_WallCommon' for a wall whose whole set lives on " +
                    "its type, which is where most exporters put it.",
                ["part_of"] =
                    "The five relations the schema enumerates, traversed RECURSIVELY. A column is " +
                    "contained in a storey which is aggregated into a building, so 'part of a building' " +
                    "is true; a single-hop implementation answers no, confidently."
            };
            return CommandResult.Ok(payload);
        }

        private static string SafeTitle(Document doc)
        {
            try { return doc == null ? null : doc.Title; } catch { return null; }
        }
    }
}
