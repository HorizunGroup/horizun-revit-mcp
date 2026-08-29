// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// horizun_audit_model — the pre-delivery health check.
//
// This is the tool you run before handing a model to a client. It answers one
// question: what in here will embarrass us? So its only job is to be true.
//
// Rules it follows that the handlers around it do not:
//
//   * NO SILENT CAPS. Every list says how many exist and how many are shown. A
//     truncated list that looks complete is how "the model is clean" gets said
//     about a model with 4,000 warnings.
//   * NO EMPTY CATCH. When a check cannot run, it says so and why, in the
//     response. A check that fails silently reads exactly like a check that
//     passed — that is worse than not running it, because it buys false calm.
//   * ORPHAN GROUP TYPES ARE COUNTED. Listing group *instances* misses group
//     types with zero instances: they carry their full geometry in the file,
//     never appear in any view, and survive Purge in older Revit. They are pure
//     invisible weight and the usual reason a model is inexplicably large.
//   * NOTHING IS SCORED AWAY. No 0-100 health index. A single number invites the
//     reader to stop reading; the findings are the deliverable.
//
// Read-only by construction: it opens no transaction, so it cannot damage the
// model it is auditing.
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
    public class AuditModelCommand : ICommand
    {
        public string Name => "horizun_audit_model";

        public string Description =>
            "Pre-delivery audit of the open model: warnings, orphan group types, in-place families, " +
            "imported (not linked) CAD, views off sheets, unplaced/redundant rooms, links, design options " +
            "and file weight. Read-only. Every count is the model's, every list states total vs. shown, " +
            "and any check that could not run is reported as failed rather than skipped silently.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""top"": { ""type"": ""integer"", ""default"": 20, ""minimum"": 1,
               ""description"": ""How many items to list per finding. Totals are always exact regardless of this."" },
    ""requirement_set"": { ""type"": ""object"",
               ""description"": ""A declarative pre-delivery gate over what the audit measured - the standard arrives here, nothing is compiled in. Known requirements: max_warnings, max_in_place_families, max_views_off_sheets, max_file_mb, max_open_mep_connectors, max_unpinned_links, max_views_without_template (numbers), forbid_orphan_group_types, forbid_imported_cad, forbid_room_problems (true enforces, false waives AND records the waiver). The reply gains a gate block with per-requirement rows and a verdict: pass, fail, or not_assessable - a check with incomplete coverage can FAIL a limit (the count is at least that) but can never PASS one, and an unknown requirement refuses the whole gate rather than reading like one that passed."" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            int top = 20;
            JObject requirementSet = null;
            JObject tolerances = null;
            JArray readinessRoles = null;
            string targetDocument = null;
            try
            {
                var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
                if (request["top"] != null) top = Math.Max(1, request.Value<int>("top"));
                requirementSet = request["requirement_set"] as JObject;
                tolerances = request["tolerances"] as JObject;
                readinessRoles = request["readiness_roles"] as JArray;
                targetDocument = (string)request["target_document"];
            }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            // AN AUDIT IS A CLAIM ABOUT A NAMED MODEL.
            //
            // This command used to take `top` and `requirement_set` and nothing else,
            // so it audited whatever document happened to be in front. A report naming
            // the wrong model is worse than no report, and every other read surface
            // here - model_scan, quantities - has required the name for exactly this
            // reason. It is a breaking change for any caller that was relying on
            // whatever was active, and a caller relying on that was relying on luck.
            if (string.IsNullOrWhiteSpace(targetDocument))
                return CommandResult.Fail(
                    "target_document is required: this command acts on the ACTIVE document, which is '" +
                    doc.Title + "'. Name the document you mean, so an audit can never be reported against a " +
                    "model nobody chose.");
            if (!string.Equals(targetDocument, doc.Title, StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail(
                    "This command acts on the active document and will NOT switch for you. Asked for '" +
                    targetDocument + "', active is '" + doc.Title + "'.");

            // The tolerances that configure the checks below. Validated BEFORE anything
            // is read, because a misspelled tolerance silently ignored leaves the check
            // running on its default while the caller believes otherwise.
            var declaredTolerances = new List<KeyValuePair<string, object>>();
            if (tolerances != null)
                foreach (JProperty p in tolerances.Properties())
                    declaredTolerances.Add(new KeyValuePair<string, object>(p.Name, ((JValue)p.Value)?.Value));
            string toleranceRefusal = PreDeliveryGateRules.ValidateTolerances(declaredTolerances);
            if (toleranceRefusal != null) return CommandResult.Fail(toleranceRefusal);

            double farRadiusMm = ToleranceOr(tolerances, CoordinateRules.ToleranceFarRadius,
                                             CoordinateRules.DefaultFarRadiusMm);
            double linkOffsetMm = ToleranceOr(tolerances, CoordinateRules.ToleranceLinkOriginOffset, 1.0);
            double levelCoincidenceMm = ToleranceOr(tolerances, DatumRules.ToleranceLevelCoincidence,
                                                    DatumRules.DefaultLevelCoincidenceMm);
            double gridCoincidenceMm = ToleranceOr(tolerances, DatumRules.ToleranceGridCoincidence,
                                                   DatumRules.DefaultGridCoincidenceMm);
            double gridAxisDegrees = ToleranceOr(tolerances, DatumRules.ToleranceGridAxis,
                                                 DatumRules.DefaultGridAxisToleranceDegrees);

            var findings = new JArray();
            var checksFailed = new JArray();

            // Each check is wrapped so one failure cannot take the audit down, but
            // the failure is REPORTED, never swallowed.
            Run(checksFailed, "warnings", () => findings.Add(Warnings(doc, top)));
            Run(checksFailed, "group_types", () => findings.Add(GroupTypes(doc, top)));
            Run(checksFailed, "in_place_families", () => findings.Add(InPlaceFamilies(doc, top)));
            Run(checksFailed, "imported_cad", () => findings.Add(ImportedCad(doc, top)));
            Run(checksFailed, "views_off_sheets", () => findings.Add(ViewsOffSheets(doc, top)));
            Run(checksFailed, "rooms", () => findings.Add(Rooms(doc, top)));
            Run(checksFailed, "links", () => findings.Add(Links(doc, top)));
            Run(checksFailed, "design_options", () => findings.Add(DesignOptions(doc, top)));
            Run(checksFailed, "open_mep_connectors", () => findings.Add(OpenMepConnectors(doc, top)));
            Run(checksFailed, "unpinned_links", () => findings.Add(UnpinnedLinks(doc, top)));
            Run(checksFailed, AuditCheckNames.ViewsWithoutTemplate, () => findings.Add(ViewsWithoutTemplate(doc, top)));

            // THE DIAGNOSTICS P0 SLICE. Each publishes named PARTS beside its count,
            // so one finding can answer several requirements - "how many levels share
            // a name" and "how many sit on top of each other" are one area and two
            // numbers, and the gate used to be able to read only one of them.
            var partCounts = new Dictionary<string, GateMeasurement>(StringComparer.Ordinal);
            Run(checksFailed, AuditCheckNames.Coordinates,
                () => findings.Add(Coordinates(doc, top, farRadiusMm, linkOffsetMm, partCounts)));
            Run(checksFailed, AuditCheckNames.Datums,
                () => findings.Add(Datums(doc, top, levelCoincidenceMm, gridCoincidenceMm, gridAxisDegrees, partCounts)));
            Run(checksFailed, AuditCheckNames.Readiness,
                () => findings.Add(Readiness(doc, top, readinessRoles, partCounts)));

            var issues = findings.Count(f => (bool)f["is_issue"]);

            // A check that RAN but could not read everything it examined. Distinct from
            // checks_failed, which is a check that died: this one produced a number, and
            // the number is a lower bound.
            var incompleteChecks = new JArray(
                findings.Where(f => f["coverage_complete"] != null && (bool)f["coverage_complete"] == false)
                        .Select(f => (JToken)new JObject
                        {
                            ["check"] = f["check"],
                            ["elements_unreadable"] = f["elements_unreadable"],
                            ["consequence"] = "'" + f["check"] + "' reports " + f["count"] +
                                              ", which is a LOWER BOUND. The elements it could not read are unknown, " +
                                              "not clean."
                        }));

            // THE THIRD WAY THIS AUDIT CAN FAIL TO SEE THE MODEL, and the only one that
            // leaves no trace in any check. A check that dies lands in checks_failed; a
            // check that cannot read an element lands in checks_with_incomplete_coverage.
            // A CLOSED WORKSET lands nowhere: its elements are not in the document, so
            // every check ran perfectly over a model with holes in it and reported clean.
            // See Core/DocumentVisibilityCoverage.cs.
            DocumentVisibilityCoverage visibility = DocumentVisibility.Measure(doc);

            // ---- the pre-delivery gate: the caller's requirement set over what was
            // MEASURED. Evaluated in Core (PreDeliveryGateRules): a lower bound can
            // fail a limit and can never pass one, an unknown requirement refuses the
            // whole gate, a waiver is recorded rather than deleted.
            JObject gate = null;
            if (requirementSet != null)
            {
                var measurements = new Dictionary<string, GateMeasurement>(StringComparer.Ordinal);
                foreach (JToken finding in findings)
                {
                    string check = (string)finding["check"];
                    if (check == null) continue;
                    measurements[check] = new GateMeasurement
                    {
                        Check = check,
                        Count = finding["count"]?.Type == JTokenType.Integer || finding["count"]?.Type == JTokenType.Float
                            ? (double?)finding.Value<double>("count") : null,
                        Ran = true,
                        CoverageComplete = finding["coverage_complete"] == null || (bool)finding["coverage_complete"]
                    };
                }
                // The named parts, attached to the finding they belong to. Without this
                // a requirement naming "datums.coincident_levels" would resolve to
                // nothing and report not_measurable forever - which is the shape of
                // the defect this slice's own gate mapping had.
                foreach (KeyValuePair<string, GateMeasurement> kv in partCounts)
                {
                    int dot = kv.Key.IndexOf('.');
                    if (dot <= 0 || dot == kv.Key.Length - 1) continue;
                    string head = kv.Key.Substring(0, dot), tail = kv.Key.Substring(dot + 1);
                    GateMeasurement parent;
                    if (!measurements.TryGetValue(head, out parent)) continue;
                    if (parent.Parts == null)
                        parent.Parts = new Dictionary<string, GateMeasurement>(StringComparer.Ordinal);
                    parent.Parts[tail] = kv.Value;
                }

                object rawSize = FileSizeMb(doc);
                double parsedSize;
                if (rawSize != null && double.TryParse(System.Convert.ToString(rawSize,
                        System.Globalization.CultureInfo.InvariantCulture),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                        out parsedSize))
                    measurements["file_size_mb"] = new GateMeasurement
                    { Check = "file_size_mb", Count = parsedSize, Ran = true, CoverageComplete = true };

                var declared = new List<KeyValuePair<string, object>>();
                foreach (JProperty property in requirementSet.Properties())
                {
                    object value;
                    switch (property.Value.Type)
                    {
                        case JTokenType.Integer: value = (long)property.Value; break;
                        case JTokenType.Float: value = (double)property.Value; break;
                        case JTokenType.Boolean: value = (bool)property.Value; break;
                        default: value = (string)property.Value; break;
                    }
                    declared.Add(new KeyValuePair<string, object>(property.Name, value));
                }
                List<GateRow> gateRows; string verdict;
                string gateError = PreDeliveryGateRules.Evaluate(declared, measurements, out gateRows, out verdict);
                if (gateError != null)
                    return CommandResult.Fail("requirement_set refused: " + gateError + " The audit itself was " +
                        "not run to completion for a gate that cannot answer.");
                gate = new JObject
                {
                    ["verdict"] = verdict,
                    ["rows"] = new JArray(gateRows.Select(r => (JToken)new JObject
                    {
                        ["requirement"] = r.Requirement,
                        ["check"] = r.Check,
                        ["limit"] = r.Limit,
                        ["measured"] = r.Measured,
                        ["status"] = r.Status,
                        ["reason"] = r.Reason
                    })),
                    ["note"] = verdict == PreDeliveryGateRules.VerdictNotAssessable
                        ? "not_assessable is not a pass: at least one requirement rests on a measurement this run could not complete."
                        : null
                };
            }

            return CommandResult.Ok(new JObject
            {
                ["model"] = SafeTitle(doc),
                ["path"] = SafePath(doc),
                ["file_size_mb"] = FileSizeMb(doc),
                ["gate"] = gate,
                ["element_count"] = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount(),
                ["checks_run"] = findings.Count,
                ["checks_failed"] = checksFailed,
                ["checks_with_incomplete_coverage"] = incompleteChecks,
                ["visibility_coverage"] = visibility.ToJson(),
                ["coverage_complete"] = checksFailed.Count == 0 && incompleteChecks.Count == 0 &&
                                        visibility.CoverageComplete,
                ["issues_found"] = issues,
                ["findings"] = findings,
                ["note"] = (checksFailed.Count > 0 || incompleteChecks.Count > 0 || !visibility.CoverageComplete)
                    ? (checksFailed.Count > 0
                          ? $"{checksFailed.Count} check(s) could not run at all — see checks_failed. "
                          : "") +
                      (incompleteChecks.Count > 0
                          ? $"{incompleteChecks.Count} check(s) RAN but could not read every element they " +
                            "examined — see checks_with_incomplete_coverage. Their counts are lower bounds. "
                          : "") +
                      (visibility.CoverageComplete ? "" : visibility.Note() + " ") +
                      "This audit is INCOMPLETE; do not read the absence of a finding as a pass."
                    : null
            });
        }


        // ==================================================================
        // THE DIAGNOSTICS P0 SLICE
        //
        // Three findings that publish named PARTS beside their count. A part is a
        // full measurement in its own right - it carries its own coverage - so the
        // lower-bound arithmetic applies to it unchanged, and a requirement can
        // name "datums.coincident_levels" instead of needing its own finding.
        // ==================================================================

        private static double ToleranceOr(JObject tolerances, string name, double fallback)
        {
            if (tolerances == null) return fallback;
            JToken t = tolerances[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            try
            {
                double v = t.Value<double>();
                return v >= 0 ? v : fallback;
            }
            catch { return fallback; }
        }

        private static GateMeasurement Part(double count, bool complete)
        {
            return new GateMeasurement { Count = count, Ran = true, CoverageComplete = complete };
        }

        /// <summary>
        /// WHERE THE MODEL THINKS IT IS. The three control points, the project
        /// location, and how far the GEOMETRY sits from the internal origin.
        ///
        /// The distance is measured from the internal origin to each element and
        /// never from a control point, which is the single most common false
        /// positive in this area: a survey point at a national grid coordinate is
        /// ten kilometres out and is CORRECT.
        /// </summary>
        private static JObject Coordinates(Document doc, int top, double farRadiusMm, double linkOffsetMm,
                                           Dictionary<string, GateMeasurement> parts)
        {
            CoordinateFacts f = DiagnosticsFacts.ReadCoordinates(doc, farRadiusMm);

            double? farthest;
            long beyond = CoordinateRules.CountBeyond(f.Outliers, farRadiusMm, out farthest);
            long reflected, rotated, offset, notSharing, linksUnreadable;
            CoordinateRules.TallyLinks(f.Links, linkOffsetMm, out reflected, out rotated, out offset,
                                       out notSharing, out linksUnreadable);

            bool coverageComplete = f.ElementsUnreadable == 0;
            var controlPoints = new GateMeasurement
            {
                Ran = true,
                CoverageComplete = true,
                Items = CoordinateRules.ReadabilityItems(f)
            };
            parts[AuditCheckNames.Coordinates + "." + CoordinateCheckParts.ControlPoints] = controlPoints;
            parts[AuditCheckNames.Coordinates + "." + CoordinateCheckParts.ElementsFarFromOrigin] =
                Part(beyond, coverageComplete);
            parts[AuditCheckNames.Coordinates + "." + CoordinateCheckParts.LinksReflected] =
                Part(reflected, linksUnreadable == 0);
            parts[AuditCheckNames.Coordinates + "." + CoordinateCheckParts.LinksRotated] =
                Part(rotated, linksUnreadable == 0);
            parts[AuditCheckNames.Coordinates + "." + CoordinateCheckParts.LinksNotSharingPosition] =
                Part(notSharing, linksUnreadable == 0);

            var items = new JArray();
            foreach (OutlierFact o in f.Outliers)
            {
                if (items.Count >= top) break;
                items.Add(new JObject
                {
                    ["element_id"] = o.ElementId,
                    ["category"] = o.Category,
                    ["name"] = o.Name,
                    ["distance_from_internal_origin_mm"] = o.DistanceMm
                });
            }

            JObject finding = Finding(AuditCheckNames.Coordinates,
                beyond > 0 || reflected > 0, (int)beyond,
                CoordinateRules.OriginNote(beyond, farRadiusMm, f.ElementsMeasured, f.ElementsUnreadable),
                items, f.Outliers.Count, (int)Math.Min(int.MaxValue, f.ElementsUnreadable));

            finding["count_means"] = "elements further than " + Fmt(farRadiusMm) +
                                     " mm from the internal origin. " + CoordinateRules.DistanceMeans;
            finding["control_points"] = PointsJson(f);
            finding["project_location"] = new JObject
            {
                ["readable"] = f.LocationReadable,
                ["active_location"] = f.ActiveLocationName,
                ["named_location_count"] = f.NamedLocationCount,
                ["angle_to_true_north_degrees"] = f.TrueNorthDegrees,
                ["why_not"] = f.LocationWhy
            };
            finding["units"] = new JObject
            {
                ["readable"] = f.UnitsReadable,
                ["length_unit"] = f.LengthUnitName
            };
            finding["geometry_extent"] = new JObject
            {
                ["elements_measured"] = f.ElementsMeasured,
                ["elements_unreadable"] = f.ElementsUnreadable,
                ["farthest_element_mm"] = f.FarthestElementMm,
                ["radius_used_mm"] = farRadiusMm,
                ["radius_is_a_default"] = tolerancesUsedDefault(farRadiusMm)
            };
            finding["links"] = new JObject
            {
                ["measured"] = f.Links.Count,
                ["transform_unreadable"] = linksUnreadable,
                ["reflected"] = reflected,
                ["rotated"] = rotated,
                ["origin_offset_beyond_tolerance"] = offset,
                ["not_sharing_host_position"] = notSharing,
                ["reflected_means"] = "a negative determinant on the link's transform. It is almost never " +
                                      "intentional and it turns every text in the link backwards.",
                ["not_sharing_host_position_means"] = "counted only where the link SAID so. A link that would " +
                                                      "not answer is not counted here, because unknown is not no."
            };
            return finding;
        }

        private static bool tolerancesUsedDefault(double radius)
        {
            return radius == CoordinateRules.DefaultFarRadiusMm;
        }

        private static JObject PointsJson(CoordinateFacts f)
        {
            return new JObject
            {
                ["internal_origin"] = PointJson(f.InternalOrigin),
                ["project_base_point"] = PointJson(f.ProjectBasePoint),
                ["survey_point"] = PointJson(f.SurveyPoint),
                ["means"] = "three DIFFERENT points. The internal origin is Revit's own (0,0,0) and cannot be " +
                            "moved; the project base point starts the project's coordinate system; the survey " +
                            "point starts the site's real-world one. Confusing them is what makes a correctly " +
                            "set-up site read as a broken model."
            };
        }

        private static JObject PointJson(PointFact p)
        {
            if (p == null) return null;
            return new JObject
            {
                ["readable"] = p.Readable,
                ["x_mm"] = p.Readable ? (JToken)Math.Round(p.XMm, 3) : JValue.CreateNull(),
                ["y_mm"] = p.Readable ? (JToken)Math.Round(p.YMm, 3) : JValue.CreateNull(),
                ["z_mm"] = p.Readable ? (JToken)Math.Round(p.ZMm, 3) : JValue.CreateNull(),
                ["distance_from_internal_origin_mm"] = p.Readable
                    ? (JToken)Math.Round(p.DistanceFromInternalOriginMm, 3) : JValue.CreateNull(),
                ["clipped"] = p.Clipped,
                ["clipped_means"] = "null because no BuiltInParameter for it compiles across Revit 2023-2027. " +
                                    "It is an admitted gap, not a reading of 'not clipped'.",
                ["why_not"] = p.Why
            };
        }

        /// <summary>
        /// LEVELS AND GRIDS. The interesting case is the near-miss: two levels a
        /// millimetre apart collide on neither name nor elevation, so nothing
        /// anywhere warns about them - and every element on the second is invisible
        /// to every schedule filtered on the first.
        /// </summary>
        private static JObject Datums(Document doc, int top, double levelCoincidenceMm, double gridCoincidenceMm,
                                      double gridAxisDegrees, Dictionary<string, GateMeasurement> parts)
        {
            long levelsUnreadable, gridsUnreadable, elementsNoLevel, elementLevelUnreadable;
            List<LevelFact> levels = DiagnosticsFacts.ReadLevels(doc, out levelsUnreadable);
            List<GridFact> grids = DiagnosticsFacts.ReadGrids(doc, out gridsUnreadable);
            DiagnosticsFacts.CountElementsPerLevel(doc, levels, out elementsNoLevel, out elementLevelUnreadable);

            List<DatumCollision> dupLevels = DatumRules.DuplicateLevelNames(levels);
            List<DatumCollision> coincidentLevels = DatumRules.CoincidentLevels(levels, levelCoincidenceMm);
            List<DatumCollision> dupGrids = DatumRules.DuplicateGridNames(grids);
            List<DatumCollision> coincidentGrids = DatumRules.CoincidentGrids(grids, gridCoincidenceMm, gridAxisDegrees);
            double? dominant;
            int onDominantAxis, angleFamilies;
            List<GridFact> offAxis = DatumRules.GridsOffAxis(grids, gridAxisDegrees, out dominant,
                                                            out onDominantAxis, out angleFamilies);
            long viewsNotMeasured, elementsNotMeasured;
            List<LevelFact> noViews = DatumRules.LevelsWithoutViews(levels, out viewsNotMeasured);
            List<LevelFact> noElements = DatumRules.LevelsWithoutElements(levels, out elementsNotMeasured);

            bool levelsComplete = levelsUnreadable == 0;
            bool gridsComplete = gridsUnreadable == 0;
            parts[AuditCheckNames.Datums + "." + DatumCheckParts.DuplicateLevelNames] = Part(dupLevels.Count, levelsComplete);
            parts[AuditCheckNames.Datums + "." + DatumCheckParts.CoincidentLevels] = Part(coincidentLevels.Count, levelsComplete);
            parts[AuditCheckNames.Datums + "." + DatumCheckParts.LevelsWithoutViews] = Part(noViews.Count, viewsNotMeasured == 0);
            parts[AuditCheckNames.Datums + "." + DatumCheckParts.LevelsWithoutElements] = Part(noElements.Count, elementsNotMeasured == 0);
            parts[AuditCheckNames.Datums + "." + DatumCheckParts.DuplicateGridNames] = Part(dupGrids.Count, gridsComplete);
            parts[AuditCheckNames.Datums + "." + DatumCheckParts.CoincidentGrids] = Part(coincidentGrids.Count, gridsComplete);
            parts[AuditCheckNames.Datums + "." + DatumCheckParts.GridsOffAxis] = Part(offAxis.Count, gridsComplete);

            int total = dupLevels.Count + coincidentLevels.Count + dupGrids.Count + coincidentGrids.Count + offAxis.Count;
            var items = new JArray();
            foreach (DatumCollision c in Concat(dupLevels, coincidentLevels, dupGrids, coincidentGrids))
            {
                if (items.Count >= top) break;
                items.Add(new JObject
                {
                    ["code"] = c.Code,
                    ["first_id"] = c.FirstId, ["first_name"] = c.FirstName,
                    ["second_id"] = c.SecondId, ["second_name"] = c.SecondName,
                    ["separation_mm"] = c.SeparationMm,
                    ["why"] = c.Why
                });
            }
            foreach (GridFact g in offAxis)
            {
                if (items.Count >= top) break;
                items.Add(new JObject
                {
                    ["code"] = "off_axis",
                    ["first_id"] = g.ElementId, ["first_name"] = g.Name,
                    ["angle_degrees"] = g.AngleDegrees,
                    ["why"] = "this grid does not lie on the building's own dominant angle of " +
                              (dominant.HasValue ? Fmt(dominant.Value) : "?") + " degrees, nor perpendicular to it."
                });
            }

            JObject finding = Finding(AuditCheckNames.Datums, total > 0, total,
                total == 0
                    ? ("no duplicate or coincident datum among " + levels.Count + " level(s) and " +
                       grids.Count + " grid(s).")
                    : (total + " datum problem(s) across " + levels.Count + " level(s) and " + grids.Count + " grid(s)."),
                items, total, (int)Math.Min(int.MaxValue, levelsUnreadable + gridsUnreadable));

            finding["levels"] = new JObject
            {
                ["measured"] = levels.Count,
                ["name_unreadable"] = levelsUnreadable,
                ["duplicate_names"] = dupLevels.Count,
                ["coincident"] = coincidentLevels.Count,
                ["without_views"] = noViews.Count,
                ["without_elements"] = noElements.Count,
                ["view_count_not_measured"] = viewsNotMeasured,
                ["element_count_not_measured"] = elementsNotMeasured,
                ["elements_with_no_level"] = elementsNoLevel,
                ["element_level_unreadable"] = elementLevelUnreadable,
                ["coincidence_tolerance_mm"] = levelCoincidenceMm
            };
            finding["grids"] = new JObject
            {
                ["measured"] = grids.Count,
                ["geometry_unreadable"] = gridsUnreadable,
                ["curved_not_evaluated"] = CountCurved(grids),
                ["duplicate_names"] = dupGrids.Count,
                ["coincident"] = coincidentGrids.Count,
                ["off_axis"] = offAxis.Count,
                ["dominant_angle_degrees"] = dominant,
                ["on_dominant_axis"] = onDominantAxis,
                ["angle_families"] = angleFamilies,
                ["angle_families_means"] = angleFamilies > 1
                    ? ("this model has " + angleFamilies + " DISTINCT grid directions. The off-axis count is " +
                       "everything that does not agree with the largest one, so it is reporting the minority " +
                       "family - which may be a rotated wing that is entirely correct. Read this number before " +
                       "reading off_axis.")
                    : "every straight grid in this model agrees with one direction and its perpendicular.",
                ["dominant_angle_means"] = "MEASURED from the grids themselves, not assumed to be zero. A " +
                                           "building rotated thirty degrees has every grid off the world axes " +
                                           "and nothing wrong with it.",
                ["coincidence_tolerance_mm"] = gridCoincidenceMm,
                ["axis_tolerance_degrees"] = gridAxisDegrees
            };
            return finding;
        }

        private static int CountCurved(List<GridFact> grids)
        {
            int n = 0;
            foreach (GridFact g in grids) if (g != null && g.IsCurved) n++;
            return n;
        }

        private static IEnumerable<DatumCollision> Concat(params List<DatumCollision>[] lists)
        {
            foreach (List<DatumCollision> l in lists)
                foreach (DatumCollision c in l) yield return c;
        }

        /// <summary>
        /// 4D AND 5D READINESS: whether the model CARRIES THE EVIDENCE a scheduler or
        /// an estimator would need. Never a connection to a programme or a cost plan.
        ///
        /// With no roles declared this reports not_assessable, which is the honest
        /// answer: no parameter naming convention is compiled in, so with nothing
        /// declared there is nothing to look for, and answering "not ready" would be
        /// this bridge inventing a standard.
        /// </summary>
        private static JObject Readiness(Document doc, int top, JArray declaredRoles,
                                         Dictionary<string, GateMeasurement> parts)
        {
            var roles = new List<ReadinessRole>();
            if (declaredRoles != null)
                foreach (JToken t in declaredRoles)
                {
                    var o = t as JObject;
                    if (o == null) continue;
                    var role = new ReadinessRole
                    {
                        Id = (string)o["id"],
                        Dimension = (string)o["dimension"],
                        BlankIsAbsent = o["blank_is_absent"] == null || o.Value<bool>("blank_is_absent")
                    };
                    var aliases = o["parameter_names"] as JArray;
                    if (aliases != null)
                        foreach (JToken a in aliases)
                        {
                            string name = (string)a;
                            if (!string.IsNullOrWhiteSpace(name)) role.Aliases.Add(name);
                        }
                    roles.Add(role);
                }

            List<string> codes;
            string refusal = ReadinessRules.Validate(roles, out codes);

            var verdicts = new List<RoleVerdict>();
            var items = new JArray();
            long unreadable = 0;

            if (refusal == null)
            {
                foreach (ReadinessRole role in roles)
                {
                    RoleMeasurement m = MeasureRole(doc, role, out unreadable);
                    RoleVerdict v = ReadinessRules.Judge(m);
                    verdicts.Add(v);
                    if (items.Count < top)
                        items.Add(new JObject
                        {
                            ["role"] = v.RoleId,
                            ["dimension"] = v.Dimension,
                            ["state"] = v.State,
                            ["matched_parameter"] = v.MatchedAlias,
                            ["elements_in_scope"] = v.ElementsInScope,
                            ["elements_carrying_a_value"] = v.ElementsCarryingValue,
                            ["elements_unreadable"] = v.ElementsUnreadable,
                            ["coverage"] = v.Coverage,
                            ["why"] = v.Why
                        });
                }
            }

            var dimensions = new JObject();
            foreach (string dim in ReadinessRules.Dimensions)
            {
                DimensionScore sc = ReadinessRules.Score(dim, verdicts);
                dimensions[dim] = new JObject
                {
                    ["state"] = sc.State,
                    ["roles_declared"] = sc.RolesDeclared,
                    ["roles_with_evidence"] = sc.RolesWithEvidence,
                    ["roles_complete"] = sc.RolesComplete,
                    ["roles_absent"] = sc.RolesAbsent,
                    ["roles_not_assessable"] = sc.RolesNotAssessable,
                    ["coverage"] = sc.Coverage,
                    ["why"] = sc.Why
                };
                var part = new GateMeasurement { Ran = refusal == null, CoverageComplete = true };
                if (refusal == null)
                {
                    var perRole = new Dictionary<string, GateItemMeasurement>(StringComparer.Ordinal);
                    foreach (RoleVerdict v in verdicts)
                    {
                        if (!string.Equals(v.Dimension, dim, StringComparison.Ordinal)) continue;
                        perRole[v.RoleId] = new GateItemMeasurement
                        {
                            Name = v.RoleId,
                            // NOT ASSESSABLE IS NULL, never false. A role nobody could
                            // measure is not a role the model failed.
                            Satisfied = v.State == ReadinessState.NotAssessable
                                ? (bool?)null
                                : v.State == ReadinessState.Complete,
                            Detail = v.Why
                        };
                    }
                    part.Items = perRole;
                }
                parts[AuditCheckNames.Readiness + "." + dim] = part;
            }

            int absent = 0;
            foreach (RoleVerdict v in verdicts) if (v.State == ReadinessState.Absent) absent++;

            JObject finding = Finding(AuditCheckNames.Readiness, absent > 0, absent,
                refusal != null
                    ? "not assessed: " + refusal
                    : (roles.Count + " declared role(s); " + absent + " carry no value on any element in scope."),
                items, roles.Count, 0);
            finding["declared"] = roles.Count;
            finding["dimensions"] = dimensions;
            finding["means"] = ReadinessRules.Means;
            if (refusal != null)
            {
                finding["not_assessed_because"] = refusal;
                finding["refusal_codes"] = new JArray(codes.ToArray());
            }
            return finding;
        }

        /// <summary>
        /// Look for a role's parameter on the elements in scope. The FIRST alias that
        /// any element carries wins, and which one it was is reported - an
        /// organisation that spells it "Cost Code" and one that spells it "CostCode"
        /// must both be measurable without either being compiled in.
        /// </summary>
        private static RoleMeasurement MeasureRole(Document doc, ReadinessRole role, out long unreadable)
        {
            unreadable = 0;
            var m = new RoleMeasurement { RoleId = role.Id, Dimension = role.Dimension };
            try
            {
                var collector = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType().WhereElementIsViewIndependent();
                foreach (Element e in collector)
                {
                    m.ElementsInScope++;
                    bool sawParameter = false, sawValue = false;
                    try
                    {
                        foreach (string alias in role.Aliases)
                        {
                            Parameter p = e.LookupParameter(alias);
                            if (p == null) continue;
                            sawParameter = true;
                            if (m.MatchedAlias == null) m.MatchedAlias = alias;
                            string v = null;
                            try { v = p.AsString() ?? p.AsValueString(); } catch { }
                            if (!string.IsNullOrWhiteSpace(v) || !role.BlankIsAbsent)
                            {
                                sawValue = true;
                                if (m.SampleValues.Count < 5 && !string.IsNullOrWhiteSpace(v) &&
                                    !m.SampleValues.Contains(v)) m.SampleValues.Add(v);
                            }
                            break;
                        }
                    }
                    catch { m.ElementsUnreadable++; unreadable++; continue; }

                    if (sawParameter) m.ParameterExists = true;
                    if (sawValue) m.ElementsCarryingValue++;
                }
            }
            catch
            {
                // The collector itself failed. Everything in scope is unreadable, which
                // Judge() turns into not_assessable rather than zero coverage.
                m.ElementsUnreadable = m.ElementsInScope;
            }
            return m;
        }

        private static string Fmt(double v)
        {
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void Run(JArray failed, string name, Action check)
        {
            try { check(); }
            catch (Exception ex)
            {
                // A check that dies quietly is indistinguishable from a check that
                // passed. Say it out loud.
                failed.Add(new JObject
                {
                    ["check"] = name,
                    ["error"] = ex.Message,
                    ["consequence"] = $"'{name}' was NOT audited. Its findings are unknown, not clean."
                });
            }
        }

        /// <summary>
        /// One check's result, INCLUDING what it could not see.
        ///
        /// checks_failed already reports a check that died outright. It says nothing
        /// about a check that ran and silently skipped elements on the way - the
        /// `catch { return false; }` inside a filter, which turns "could not read this
        /// one" into "this one is fine". A count of 0 then reads as a pass when it
        /// means "none found among the ones I could read".
        ///
        /// elements_unreadable is that number, per check. coverage_complete is the
        /// single field to look at: false means the count below it is a LOWER BOUND.
        /// </summary>
        private static JObject Finding(string check, bool isIssue, int count, string summary, JArray items, int total,
                                       int elementsUnreadable = 0)
        {
            return new JObject
            {
                ["check"] = check,
                // An unreadable element cannot be ruled out as an issue, so a check that
                // could not read everything is not allowed to report a clean result.
                ["is_issue"] = isIssue || elementsUnreadable > 0,
                ["count"] = count,
                ["count_is_lower_bound"] = elementsUnreadable > 0,
                ["elements_unreadable"] = elementsUnreadable,
                ["coverage_complete"] = elementsUnreadable == 0,
                ["coverage_note"] = elementsUnreadable == 0
                    ? null
                    : elementsUnreadable + " element(s) could not be read by this check and are counted in neither " +
                      "column. They are NOT known to be clean - 'count' is a lower bound, and this check is " +
                      "reported as an issue for that reason alone.",
                ["summary"] = summary,
                ["shown"] = items?.Count ?? 0,
                ["total"] = total,
                ["truncated"] = items != null && items.Count < total,
                ["items"] = items
            };
        }

        // ---- Warnings: the model's own list of what it knows is wrong. ----
        private static JObject Warnings(Document doc, int top)
        {
            var all = doc.GetWarnings();
            var grouped = all
                .GroupBy(w => { try { return w.GetDescriptionText(); } catch { return "(description unavailable)"; } })
                .Select(g => new { desc = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count)
                .ToList();

            var items = new JArray(grouped.Take(top).Select(g => (JToken)new JObject
            {
                ["description"] = g.desc,
                ["occurrences"] = g.count
            }));

            return Finding(AuditCheckNames.Warnings, all.Count > 0, all.Count,
                all.Count == 0
                    ? "No warnings."
                    : $"{all.Count} warning(s) across {grouped.Count} distinct message(s). Warnings are Revit " +
                      "telling you the model already contradicts itself; they do not resolve themselves.",
                items, grouped.Count);
        }

        // ---- Group types with zero instances: invisible file weight. ----
        private static JObject GroupTypes(Document doc, int top)
        {
            // A Group whose GetTypeId cannot be read leaves its type looking UNPLACED,
            // because nothing added it to this set - so the check invents an orphan.
            // Counted instead of swallowed: the orphan count becomes a lower bound and
            // the check reports incomplete coverage.
            int unreadable = 0;
            var placed = new HashSet<ElementId>();
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>())
            {
                try { placed.Add(g.GetTypeId()); } catch { unreadable++; }
            }

            var orphans = new FilteredElementCollector(doc)
                .OfClass(typeof(GroupType))
                .Cast<GroupType>()
                .Where(gt => !placed.Contains(gt.Id))
                .ToList();

            var items = new JArray(orphans.Take(top).Select(gt => (JToken)new JObject
            {
                ["group_type_id"] = gt.Id.ToString(),
                ["name"] = SafeName(gt),
                ["members"] = SafeMemberCount(gt)
            }));

            return Finding(AuditCheckNames.OrphanGroupTypes, orphans.Count > 0, orphans.Count,
                orphans.Count == 0
                    ? (unreadable == 0
                        ? "Every group type is placed at least once."
                        : $"Every group type is placed at least once among the {unreadable} group(s) whose type " +
                          "could be read - and those that could NOT be read are not accounted for.")
                    : $"{orphans.Count} group type(s) exist with ZERO placed instances. They carry their full " +
                      "geometry in the file, appear in no view, and are the usual reason a model is " +
                      "inexplicably large. Listing group instances never finds these." +
                      (unreadable > 0
                          ? $" CAUTION: {unreadable} group(s) would not report their type, so a type they place " +
                            "may be listed here as an orphan that is not one."
                          : ""),
                items, orphans.Count, unreadable);
        }

        // ---- In-place families: the classic performance and coordination tax. ----
        private static JObject InPlaceFamilies(Document doc, int top)
        {
            // The catch here used to `return false`, which quietly EXCLUDED any instance
            // whose Symbol or Family could not be read - so an in-place family that
            // happened to be unreadable was reported as absent, and a count of 0 meant
            // "none found or none readable" while saying "none". Unreadable is now
            // counted and reported beside the count, not folded into it.
            int unreadable = 0;
            var inPlace = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi =>
                {
                    try { return fi.Symbol?.Family?.IsInPlace == true; }
                    catch { unreadable++; return false; }
                })
                .ToList();

            var grouped = inPlace
                .GroupBy(fi => { try { return fi.Symbol.Family.Name; } catch { return "(unnamed)"; } })
                .Select(g => new { name = g.Key, count = g.Count(), id = g.First().Id })
                .OrderByDescending(x => x.count)
                .ToList();

            var items = new JArray(grouped.Take(top).Select(g => (JToken)new JObject
            {
                ["family"] = g.name,
                ["instances"] = g.count,
                ["example_id"] = g.id.ToString()
            }));

            return Finding(AuditCheckNames.InPlaceFamilies, inPlace.Count > 0, inPlace.Count,
                inPlace.Count == 0
                    ? (unreadable == 0
                        ? "No in-place families."
                        : $"No in-place families among the instances that could be read - but {unreadable} could " +
                          "NOT be read, so this is not a clean bill.")
                    : $"{inPlace.Count} in-place family instance(s) in {grouped.Count} family(ies). In-place " +
                      "geometry cannot be scheduled reliably, cannot be reused, and is recomputed on every " +
                      "regeneration. Each one is a loadable family somebody chose not to make.",
                items, grouped.Count, unreadable);
        }

        // ---- Open MEP connectors: every unconnected end in the model. ----
        // An open connector is a run that stops mid-air: no flow, no system
        // totals, and a coordination surprise for whoever meets the stub. The
        // census sweeps MEP curves AND connectable family instances - the same
        // reader query_model's include_mep uses - and counts what it could not
        // read instead of folding it into the total.
        private static JObject OpenMepConnectors(Document doc, int top)
        {
            int unreadable = 0, elementsWithConnectors = 0, openTotal = 0;
            var worst = new List<KeyValuePair<Element, int>>();
            var collectors = new List<FilteredElementCollector>
            {
                new FilteredElementCollector(doc).OfClass(typeof(MEPCurve)),
                new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
            };
            foreach (FilteredElementCollector collector in collectors)
                foreach (Element element in collector)
                {
                    ConnectorManager manager;
                    try { manager = MepFacts.ManagerOf(element); }
                    catch { unreadable++; continue; }
                    if (manager == null) continue;
                    int open = 0;
                    try
                    {
                        foreach (Connector connector in MepFacts.Ordered(manager))
                            if (!connector.IsConnected) open++;
                    }
                    catch { unreadable++; continue; }
                    elementsWithConnectors++;
                    if (open > 0)
                    {
                        openTotal += open;
                        worst.Add(new KeyValuePair<Element, int>(element, open));
                    }
                }

            var items = new JArray(worst
                .OrderByDescending(pair => pair.Value)
                .Take(top)
                .Select(pair => (JToken)new JObject
                {
                    ["element_id"] = pair.Key.Id.ToString(),
                    ["category"] = SafeCategoryName(pair.Key),
                    ["open_connectors"] = pair.Value
                }));

            return Finding(AuditCheckNames.OpenMepConnectors, openTotal > 0, openTotal,
                openTotal == 0
                    ? (unreadable == 0
                        ? $"Every connector on {elementsWithConnectors} connectable element(s) is connected."
                        : $"No open connectors among what could be read - but {unreadable} element(s) could NOT " +
                          "be read, so this is not a clean bill.")
                    : $"{openTotal} open connector(s) across {worst.Count} element(s) (of {elementsWithConnectors} " +
                      "carrying connectors). An open connector is a run that stops mid-air: no flow calculation " +
                      "crosses it and whoever coordinates against it meets a stub.",
                items, worst.Count, unreadable);
        }

        // ---- Unpinned links: a link somebody can drag is a coordination accident
        // waiting. Each finding NAMES ITS TYPED CORRECTION - the exact command and
        // arguments that fix it - so an audit is a work list, not a lament.
        private static JObject UnpinnedLinks(Document doc, int top)
        {
            int unreadable = 0;
            var unpinned = new List<RevitLinkInstance>();
            foreach (RevitLinkInstance instance in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkInstance)).OfType<RevitLinkInstance>())
            {
                try { if (!instance.Pinned) unpinned.Add(instance); }
                catch { unreadable++; }
            }
            var items = new JArray(unpinned.Take(top).Select(instance => (JToken)new JObject
            {
                ["element_id"] = instance.Id.ToString(),
                ["name"] = SafeName(instance),
                ["correction"] = new JObject
                {
                    ["tool"] = "horizun_manage_links",
                    ["arguments"] = new JObject
                    {
                        ["target_document"] = doc.Title,
                        ["operation"] = "pin",
                        ["link_instance_id"] = Rid.Value(instance.Id)
                    }
                }
            }));
            return Finding(AuditCheckNames.UnpinnedLinks, unpinned.Count > 0, unpinned.Count,
                unpinned.Count == 0
                    ? "Every link instance is pinned."
                    : unpinned.Count + " link instance(s) are not pinned; each row names the typed pin that fixes it.",
                items, unpinned.Count, unreadable);
        }

        // ---- Views without a template: every one is a hand-formatted view. The
        // correction names the command and leaves template_id as an EXPLICIT hole -
        // choosing the template is the person's decision, not the audit's.
        private static JObject ViewsWithoutTemplate(Document doc, int top)
        {
            int unreadable = 0;
            var bare = new List<View>();
            foreach (View view in new FilteredElementCollector(doc).OfClass(typeof(View)).OfType<View>())
            {
                try
                {
                    if (view.IsTemplate || !view.CanBePrinted) continue;
                    if (view.ViewTemplateId == ElementId.InvalidElementId) bare.Add(view);
                }
                catch { unreadable++; }
            }
            var items = new JArray(bare.Take(top).Select(view => (JToken)new JObject
            {
                ["element_id"] = view.Id.ToString(),
                ["name"] = SafeName(view),
                ["correction"] = new JObject
                {
                    ["tool"] = "horizun_manage_views",
                    ["arguments"] = new JObject
                    {
                        ["target_document"] = doc.Title,
                        ["actions"] = new JArray(new JObject
                        {
                            ["key"] = "template-" + Rid.Value(view.Id),
                            ["operation"] = "apply_template",
                            ["view_id"] = Rid.Value(view.Id),
                            ["template_id"] = "<CHOOSE: the template this view should follow>"
                        })
                    }
                }
            }));
            return Finding(AuditCheckNames.ViewsWithoutTemplate, bare.Count > 0, bare.Count,
                bare.Count == 0
                    ? "Every printable view follows a template."
                    : bare.Count + " printable view(s) follow no template; each row names the typed correction " +
                      "with template_id left as the person's explicit choice.",
                items, bare.Count, unreadable);
        }

        private static string SafeCategoryName(Element element)
        {
            try { return element?.Category?.Name; } catch { return null; }
        }

        // ---- Imported vs linked CAD. Imported DWG is permanent weight. ----
        private static JObject ImportedCad(Document doc, int top)
        {
            // Same rule: an ImportInstance whose IsLinked cannot be read is UNKNOWN, and
            // unknown is not "linked, therefore fine".
            int unreadable = 0;
            var imports = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .Where(i => { try { return !i.IsLinked; } catch { unreadable++; return false; } })
                .ToList();

            var items = new JArray(imports.Take(top).Select(i => (JToken)new JObject
            {
                ["id"] = i.Id.ToString(),
                ["name"] = SafeName(i),
                ["view_specific"] = SafeViewSpecific(i)
            }));

            return Finding(AuditCheckNames.ImportedCad, imports.Count > 0, imports.Count,
                imports.Count == 0
                    ? (unreadable == 0
                        ? "No imported (non-linked) CAD."
                        : $"No imported CAD among the instances that could be read - but {unreadable} could NOT be " +
                          "read, so this is not a clean bill.")
                    : $"{imports.Count} CAD file(s) IMPORTED rather than linked. An import is permanent: its " +
                      "layers, line patterns and text styles are now part of this model's namespace and " +
                      "survive deletion of the instance. A link stays outside and can be reloaded or dropped.",
                items, imports.Count, unreadable);
        }

        // ---- Views that are not on any sheet: work nobody will ever see. ----
        private static JObject ViewsOffSheets(Document doc, int top)
        {
            // TWO silent catches lived here, and they failed in OPPOSITE directions.
            //
            // A Viewport whose ViewId could not be read left its view missing from
            // onSheet, so a view that IS on a sheet gets reported as off-sheet - a
            // fabricated finding. A View whose properties could not be read returned
            // false and vanished from the count - a hidden one. Both are now counted,
            // and either makes the check report incomplete coverage.
            int unreadable = 0;
            var onSheet = new HashSet<ElementId>();
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                try { onSheet.Add(vp.ViewId); } catch { unreadable++; }
            }

            var candidates = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v =>
                {
                    try
                    {
                        if (v.IsTemplate) return false;
                        if (v is ViewSheet) return false;
                        // Schedules and legends can legitimately live off-sheet mid-project;
                        // 3D/plan/section views off-sheet are the ones that pile up.
                        if (v.ViewType == ViewType.Legend || v.ViewType == ViewType.Schedule) return false;
                        if (v.ViewType == ViewType.DrawingSheet || v.ViewType == ViewType.Internal) return false;
                        return !onSheet.Contains(v.Id);
                    }
                    catch { unreadable++; return false; }
                })
                .ToList();

            var items = new JArray(candidates.Take(top).Select(v => (JToken)new JObject
            {
                ["id"] = v.Id.ToString(),
                ["name"] = SafeName(v),
                ["type"] = v.ViewType.ToString()
            }));

            return Finding(AuditCheckNames.ViewsOffSheets, candidates.Count > 0, candidates.Count,
                candidates.Count == 0
                    ? "Every non-legend, non-schedule view is placed on a sheet."
                    : $"{candidates.Count} view(s) are on no sheet. Some are working views and that is fine — " +
                      "this is a list to review before delivery, not a defect list. Legends and schedules are " +
                      "excluded on purpose." +
                      (unreadable > 0
                          ? $" CAUTION: {unreadable} viewport(s) or view(s) could not be read. A viewport that " +
                            "would not name its view makes the view it holds look off-sheet, so this list may " +
                            "contain views that are placed."
                          : ""),
                items, candidates.Count, unreadable);
        }

        // ---- Rooms: unplaced and redundant both corrupt area takeoffs. ----
        private static JObject Rooms(Document doc, int top)
        {
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .ToElements();

            // A room that could not be read is not a room that is fine. This catch used
            // to drop it, so a model whose rooms all failed to read reported "all rooms
            // are placed and enclosed" - a clean bill issued over nothing.
            int unreadable = 0;
            var bad = new List<(Element e, string why)>();
            foreach (var r in rooms)
            {
                try
                {
                    var area = r.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsDouble() ?? 0.0;
                    var loc = r.Location;
                    if (loc == null) bad.Add((r, "unplaced (no location — it exists in schedules but bounds nothing)"));
                    else if (area <= 0.0) bad.Add((r, "not enclosed (area 0 — its boundary is open, so it measures nothing)"));
                }
                catch { unreadable++; }
            }

            var items = new JArray(bad.Take(top).Select(b => (JToken)new JObject
            {
                ["id"] = b.e.Id.ToString(),
                ["name"] = SafeName(b.e),
                ["problem"] = b.why
            }));

            return Finding(AuditCheckNames.Rooms, bad.Count > 0, bad.Count,
                rooms.Count == 0
                    ? "No rooms in this model."
                    : bad.Count == 0
                        ? (unreadable == 0
                            ? $"All {rooms.Count} room(s) are placed and enclosed."
                            : $"Of {rooms.Count} room(s), the ones that could be read are placed and enclosed - but " +
                              $"{unreadable} could NOT be read, so this is not a clean bill.")
                        : $"{bad.Count} of {rooms.Count} room(s) are unplaced or unenclosed. Both still appear " +
                          "in room schedules — with an area of zero. Any area takeoff from this model is " +
                          "understated until they are fixed." +
                          (unreadable > 0 ? $" A further {unreadable} could not be read at all." : ""),
                items, bad.Count, unreadable);
        }

        // ---- Links: an unloaded link is a coordination hole. ----
        private static JObject Links(Document doc, int top)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .ToList();

            // Read each status ONCE, keeping null for "would not answer". This used to be
            // read twice per link, and the second read wrote `catch { return true; }` -
            // counting a failed read as an unloaded link. On a cloud-hosted model that
            // fabricates a defect: the link is loaded, we just could not ask.
            var statuses = new List<string>(types.Count);
            foreach (var lt in types)
            {
                string s = null;
                try { s = lt.GetLinkedFileStatus().ToString(); } catch { s = null; }
                statuses.Add(s);
            }

            var items = new JArray();
            for (int i = 0; i < types.Count && i < top; i++)
                items.Add(new JObject
                {
                    ["id"] = types[i].Id.ToString(),
                    ["name"] = SafeName(types[i]),
                    ["status"] = statuses[i] ?? "(unreadable)",
                    ["status_unreadable"] = statuses[i] == null
                });

            LinkTally tally = LinkStatusTally.Of(statuses);

            // An issue when a link is genuinely not loaded, AND when coverage is partial:
            // "I could not check" is a finding to review, not a pass.
            // This check tallies link TYPES; model_scan's links section tallies link
            // INSTANCES. On a model with the same link loaded several times the two
            // numbers differ ("1 of 8" vs "4 of 22", measured 2026-07-30) and both are
            // right - so each summary now names its unit instead of making the reader
            // guess which one is lying.
            // `count` is the number of findings, not the population examined.
            // Returning types.Count here made a healthy model report (for example)
            // 18 link issues while its own summary correctly said all 18 were loaded.
            // `total` below already carries the population that was examined.
            return Finding(AuditCheckNames.Links, tally.NotLoaded > 0 || !tally.Complete, tally.NotLoaded,
                tally.Summary("link type"), items, types.Count);
        }

        // ---- Design options: geometry that is in the file but not in the delivery. ----
        private static JObject DesignOptions(Document doc, int top)
        {
            var opts = new FilteredElementCollector(doc)
                .OfClass(typeof(DesignOption))
                .Cast<DesignOption>()
                .ToList();

            var items = new JArray(opts.Take(top).Select(o => (JToken)new JObject
            {
                ["id"] = o.Id.ToString(),
                ["name"] = SafeName(o),
                ["is_primary"] = SafePrimary(o)
            }));

            return Finding(AuditCheckNames.DesignOptions, opts.Count > 0, opts.Count,
                opts.Count == 0
                    ? "No design options."
                    : $"{opts.Count} design option(s) present. Elements in a non-primary option are in the file " +
                      "and in nobody's takeoff. Confirm this is intended before delivering.",
                items, opts.Count);
        }

        // ---- Small, boring, and each one honest about failing. ----
        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
        private static string SafePath(Document d) { try { return d.PathName; } catch { return null; } }
        private static string SafeName(Element e) { try { return e?.Name; } catch { return null; } }
        private static JToken SafePrimary(DesignOption o) { try { return o.IsPrimary; } catch { return null; } }
        private static JToken SafeViewSpecific(Element e) { try { return e.ViewSpecific; } catch { return null; } }
        private static JToken SafeMemberCount(GroupType gt)
        {
            try
            {
                var g = gt.Groups?.Cast<Group>().FirstOrDefault();
                return g?.GetMemberIds()?.Count;
            }
            catch { return null; }
        }

        private static JToken FileSizeMb(Document doc)
        {
            try
            {
                var p = doc.PathName;
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return null;
                return Math.Round(new FileInfo(p).Length / 1048576.0, 2);
            }
            catch { return null; }
        }
    }
}
