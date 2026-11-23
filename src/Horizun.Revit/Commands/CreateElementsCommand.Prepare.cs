using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    // Request -> validated plans, before any confirmation or transaction. Kept apart from the
    // Revit construction code so the request rules run under the Core test doubles.
    public sealed partial class CreateElementsCommand
    {
        private static CommandResult PrepareCreation(Document doc, JObject request, out CreationBatch batch)
        {
            batch = null;
            ResponseSummaryRules.ParseMode(request, out string responseModeError);
            if (responseModeError != null) return CommandResult.Fail(responseModeError);
            string validationMode = request.Value<string>("validation_mode") ?? "arguments";
            if (validationMode != "arguments" && validationMode != "revit_rollback") return CommandResult.Fail("validation_mode must be arguments or revit_rollback.");

            JArray input = request["elements"] as JArray;
            JObject tabularBlock = null;
            if (request["tabular_source"] is JObject tabularSource)
            {
                // A CSV becomes family_instance entries, one per data row, expanded
                // HERE on every call: a file edited between rehearsal and apply
                // expands differently, the resolved plans differ, and the token
                // refuses stale - the same binding the parameter writer proved.
                if (input != null)
                    return CommandResult.Fail("give elements OR tabular_source, not both: a batch with two " +
                        "sources of truth cannot say which file row produced which element.");
                double preScale;
                if (!Scale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out preScale))
                    return CommandResult.Fail("units must be mm, m or feet.");
                string expandError = ExpandTabularPlacements(doc, tabularSource, preScale, out input, out tabularBlock);
                if (expandError != null) return CommandResult.Fail(expandError + " Nothing ran.");
            }
            // Expand architectural recipes into the same protected typed plans as ordinary rows.
            double architectureScale;
            if (!Scale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out architectureScale)) return CommandResult.Fail("units must be mm, m or feet.");
            JArray architectureBlock;
            try
            {
                input = ArchitectureCreationRules.Expand(input, doc.Application.ShortCurveTolerance / architectureScale,
                    (row, kind, notes) => ResolveHostedRecipe(doc, row, kind, notes), out architectureBlock);
            }
            catch (ArgumentException ex) { return CommandResult.Fail(ex.Message + " Nothing ran."); }
            if (input?.Count == 0 && architectureBlock.Count > 0)
                return CreationResponse(CommandResult.RefuseBeforeWrite("Every wall_chain segment was below the curve tolerance.",
                    ErrorCodes.PrecondicionNoCumplida), Enumerable.Empty<Plan>(), architectureBlock);

            // placement='all_enclosed' rows become one room/space row per circuit (CreateElementsEnclosed.cs).
            JArray enclosedBlock = null;
            if (input != null)
            {
                string enclosedError = ExpandEnclosed(doc, request, ref input, out enclosedBlock);
                if (enclosedError != null) return CommandResult.Fail(enclosedError + " Nothing ran.");
                if (enclosedBlock != null && input.Count == 0) return NothingEnclosed(request, enclosedBlock);
            }
            if (input == null || input.Count == 0) return CommandResult.Fail("elements is required and must be non-empty.");
            if (input.Count > Horizun.Contracts.Contract.CreateElementsBatchLimit)
                return CommandResult.Fail($"elements exceeds the {Horizun.Contracts.Contract.CreateElementsBatchLimit} item atomic-batch limit.");
            if(input.Count!=1 && input.OfType<JObject>().Any(x=>x.Value<string>("kind")=="stairs"))
                return CommandResult.Fail("stairs uses StairsEditScope and must be the sole element in its batch.");
            double scale;
            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            if (!Scale(units, out scale)) return CommandResult.Fail("units must be mm, m or feet.");

            var rows = Horizun.Revit.Core.CreationBatch<Plan>.Plan(input, (i, item, kind) =>
            {
                Plan plan = PlanItem(doc, i, item, kind, scale, out string error, out string reason);
                if (plan != null)
                {
                    error = LevelSpanCreationError(doc, plan, request.Value<string>("level_span_exception"));
                    if (error != null) plan = null;
                }
                return (plan, error, reason);
            });
            var plans = rows.Plans;
            var errors = rows.Errors;
            var outcomes = rows.Outcomes;

            // THE LOCAL MEP GRAPH, judged before Revit builds anything (Core/MepGraphRules.cs): a tee over three collinear
            // axes, an elbow that returns 180 degrees on itself, fittings over curves that do not meet. Refusals are row
            // errors of the fitting row; warnings (tight curve between fittings, mixed sizes at an elbow, a type with no
            // routing preference) travel in the rehearsal reply.
            List<MepGraphIssue> mepGraphIssues = MepGraphCheck(doc, input, scale);
            if (errors.Count == 0 && !mepGraphIssues.Any(issue => issue.Refusal))
            {
                try
                {
                    MepGraphIssue diameterIssue = ResolveConduitDiameters(doc, plans);
                    if (diameterIssue != null) mepGraphIssues.Add(diameterIssue);
                }
                catch (DiameterProbeRollbackFailed ex)
                {
                    return CommandResult.FailWithDetail(ex.Message, new JObject
                    {
                        ["state"] = "uncertain",
                        ["write_started"] = true,
                        ["changes_applied"] = JValue.CreateNull(),
                        ["rollback_status"] = "unconfirmed",
                        ["rollback_error"] = ex.InnerException?.Message
                    });
                }
                catch (Exception ex)
                {
                    return CreationResponse(CommandResult.Fail("SUPUESTO: could not resolve the omitted run diameter. " + ex.Message),
                        plans, architectureBlock);
                }
            }
            foreach (MepGraphIssue issue in mepGraphIssues.Where(x => x.Refusal))
            {
                if (errors.OfType<JObject>().Any(e => e.Value<int?>("index") == issue.Index)) continue;
                string message = issue.Code + ": " + issue.Message;
                plans.RemoveAll(pl => pl.Index == issue.Index);
                rows.RefuseRow(issue.Index, message, Horizun.Revit.Core.CreationBatch<Plan>.SourceIndex(input[issue.Index] as JObject));
            }
            var mepGraphWarnings = new JArray(mepGraphIssues.Where(x => !x.Refusal).Select(x => (JToken)x.ToJson()));

            batch = new CreationBatch
            {
                Plans = plans, Errors = errors, InputCount = input.Count, ValidationMode = validationMode,
                Tabular = tabularBlock, Enclosed = enclosedBlock, Architecture = architectureBlock,
                Outcomes = outcomes, MepGraphWarnings = mepGraphWarnings
            };
            return null;
        }

        private static string CreationPlanHash(JObject request)
        {
            return DocumentGate.PlanHash(request, "units", "elements", "tabular_source", "level_span_exception");
        }

        // ---- CSV rows into family_instance entries. -----------------------------
        private static string ExpandTabularPlacements(Document doc, JObject source, double scale,
                                                      out JArray elements, out JObject provenance)
        {
            elements = null; provenance = null;
            string path = source.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path))
                return "tabular_source.path is required and must be absolute.";
            if (!System.IO.File.Exists(path)) return "tabular_source file '" + path + "' does not exist.";
            string decimalSeparator = source.Value<string>("decimal_separator") ?? ".";
            if (decimalSeparator != "." && decimalSeparator != ",")
                return "decimal_separator must be '.' or ',' - it is DECLARED, never guessed from the file.";
            string coordinates = (source.Value<string>("coordinates") ?? "internal").ToLowerInvariant();
            if (coordinates != "internal" && coordinates != "shared")
                return "coordinates must be internal or shared.";
            long typeId = source.Value<long?>("type_id") ?? -1;
            if (!Rid.CanRepresent(typeId) || !(doc.GetElement(Rid.Make(typeId)) is FamilySymbol))
                return "tabular_source.type_id must identify the FamilySymbol every row places.";
            long levelId = source.Value<long?>("level_id") ?? -1;
            if (!Rid.CanRepresent(levelId) || !(doc.GetElement(Rid.Make(levelId)) is Level))
                return "tabular_source.level_id must identify the Level every row lands on.";

            return ReadPlacementCsv(path, source, decimalSeparator, coordinates, typeId, levelId, scale,
                () => ((Level)doc.GetElement(Rid.Make(levelId))).ProjectElevation, ReadProjectPosition,
                out elements, out provenance);

            (double Angle, double EastWest, double NorthSouth, double Elevation)? ReadProjectPosition()
            {
                try
                {
                    var position = doc.ActiveProjectLocation?.GetProjectPosition(XYZ.Zero);
                    return position == null ? null : (position.Angle, position.EastWest, position.NorthSouth, position.Elevation);
                }
                catch { return null; }
            }
        }

        private static bool Scale(string units, out double scale)
        { if (units == "feet") { scale = 1; return true; } if (units == "m") { scale = 1 / 0.3048; return true; } if (units == "mm") { scale = 1 / 304.8; return true; } scale = 0; return false; }
    }
}
