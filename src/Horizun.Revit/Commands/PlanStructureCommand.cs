// -----------------------------------------------------------------------------
// Horizun Revit MCP - deterministic, read-only STRUCTURAL layout planning.
//
// horizun_plan_structure answers "column this grid, frame these bays" the way
// plan_views answers room production: MEASURE the grids and what already
// stands, DECIDE with rules proved in Core/StructuralLayoutRules.cs, and
// return ordered horizun_create_elements requests the caller inspects,
// rehearses and applies. This command writes NOTHING - create_elements stays
// the single rehearsed, confirmed and re-read write path.
//
// The account is the point: every crossing found, every crossing an existing
// column already occupies (measured by DISTANCE, never by name), every span a
// beam already covers, every span too short to be real - each with its code,
// and a coverage verdict that is never optimistic.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;
using System.Globalization;

namespace Horizun.Revit.Commands
{
    public sealed class PlanStructureCommand : ICommand
    {
        public string Name => "horizun_plan_structure";
        public string Description =>
            "Plan structural columns on grid intersections and framing between consecutive intersections, " +
            "deduplicated against what already stands, with bounded horizun_create_elements dry-run batches. Read-only.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");
            CommandResult wrongDocument = DocumentGate.ReadGuard(doc, request, Name);
            if (wrongDocument != null) return wrongDocument;

            string operation = (request.Value<string>("operation") ?? "").ToLowerInvariant();
            if (operation != "columns_on_grid_intersections" && operation != "beams_along_grids")
                return CommandResult.Fail("operation must be columns_on_grid_intersections or beams_along_grids.");

            // ---- shared resolution ------------------------------------------------
            long levelId = request.Value<long?>("level_id") ?? -1;
            Level level = Rid.CanRepresent(levelId) ? doc.GetElement(Rid.Make(levelId)) as Level : null;
            if (level == null) return CommandResult.Fail("level_id must identify a Level; placements land on it.");

            long typeId = request.Value<long?>("type_id") ?? -1;
            FamilySymbol symbol = Rid.CanRepresent(typeId) ? doc.GetElement(Rid.Make(typeId)) as FamilySymbol : null;
            BuiltInCategory needed = operation == "columns_on_grid_intersections"
                ? BuiltInCategory.OST_StructuralColumns : BuiltInCategory.OST_StructuralFraming;
            if (symbol == null || symbol.Category == null || Rid.Value(symbol.Category.Id) != (long)needed)
                return CommandResult.Fail("type_id must identify a FamilySymbol in " +
                    (needed == BuiltInCategory.OST_StructuralColumns ? "OST_StructuralColumns" : "OST_StructuralFraming") + ".");

            List<GridSegment> segments;
            JArray unplannable = new JArray();
            string gridError = CollectGrids(doc, request, segments: out segments, unplannable);
            if (gridError != null) return CommandResult.Fail(gridError);
            if (segments.Count < 2)
                return CommandResult.Fail("fewer than two usable straight grids were found (" + segments.Count +
                    "); nothing can cross. " + (unplannable.Count > 0 ? "Unusable: " + unplannable.ToString() : ""));

            List<GridIntersection> crossings = StructuralLayoutRules.Intersections(segments);
            var result = new JObject
            {
                ["operation"] = operation,
                ["level"] = new JObject { ["id"] = Rid.Value(level.Id), ["name"] = level.Name },
                ["type"] = new JObject { ["id"] = Rid.Value(symbol.Id), ["name"] = SafeName(symbol) },
                ["grids_used"] = segments.Count,
                ["grids_unusable"] = unplannable,
                ["intersections_found"] = crossings.Count
            };
            if (crossings.Count == 0)
            {
                result["coverage"] = "nothing_found";
                result["note"] = "the selected grids do not cross within their drawn extents; nothing was planned.";
                return CommandResult.Ok(result);
            }

            if (operation == "columns_on_grid_intersections")
                return PlanColumns(doc, request, level, symbol, crossings, result);
            return PlanBeams(doc, request, level, symbol, segments, crossings, result);
        }

        /// <summary>The level's height in internal coordinates, the frame geometry is read in.</summary>
        private static double LevelGeometryHeight(Level level)
            => StructuralLayoutRules.GeometryHeight(level.Elevation, level.ProjectElevation);

        /// <summary>Every other level's height in internal coordinates: they narrow the band a member is matched in.</summary>
        private static List<double> OtherLevelHeights(Document doc, Level level)
        {
            var z = new List<double>();
            foreach (Level other in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
            {
                try { if (other.Id != level.Id) z.Add(LevelGeometryHeight(other)); }
                catch { }
            }
            return z;
        }

        // ------------------------------------------------------------------ columns
        private CommandResult PlanColumns(Document doc, JObject request, Level level, FamilySymbol symbol,
                                          List<GridIntersection> crossings, JObject result)
        {
            // Existing structural columns, by measured position at this level's storey.
            double levelZ = LevelGeometryHeight(level);
            List<double> otherLevelZs = OtherLevelHeights(doc, level);
            var existing = new List<double[]>();
            foreach (FamilyInstance column in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType()
                         .OfType<FamilyInstance>())
            {
                try
                {
                    if (!(column.Location is LocationPoint location)) continue;
                    // Only a column that stands on THIS storey makes a crossing "already present":
                    // columns of the storey below stop at this level and the next storey still needs its own.
                    // Heights in INTERNAL coordinates on both sides: the box is, and ProjectElevation is.
                    BoundingBoxXYZ box = column.get_BoundingBox(null);
                    bool stands = box != null
                        ? StructuralLayoutRules.ColumnOccupiesLevel(box.Min.Z, box.Max.Z, levelZ, otherLevelZs)
                        : column.LevelId == level.Id;
                    if (stands) existing.Add(new[] { location.Point.X, location.Point.Y });
                }
                catch { }
            }

            List<GridIntersection> toPlace, alreadyPresent;
            StructuralLayoutRules.DedupColumns(crossings, existing, out toPlace, out alreadyPresent);

            var elements = new JArray();
            var planned = new JArray();
            foreach (GridIntersection crossing in toPlace)
            {
                planned.Add(new JObject
                {
                    ["at"] = StructuralLayoutRules.Describe(crossing),
                    ["point_mm"] = new JArray(Math.Round(crossing.X * 304.8, 1), Math.Round(crossing.Y * 304.8, 1))
                });
                elements.Add(new JObject
                {
                    ["kind"] = "structural_column",
                    ["coordinate_mode"] = "level_offset",
                    ["type_id"] = Rid.Value(symbol.Id),
                    ["level_id"] = Rid.Value(level.Id),
                    ["point"] = new JArray(Math.Round(crossing.X * 304.8, 1), Math.Round(crossing.Y * 304.8, 1), 0)
                });
            }
            var omitted = new JArray(alreadyPresent.Select(crossing => new JObject
            {
                ["at"] = StructuralLayoutRules.Describe(crossing),
                ["code"] = StructuralLayoutRules.CodeAlreadyPresent,
                ["reason"] = "a structural column already stands within " +
                             Math.Round(StructuralLayoutRules.SamePlaceToleranceFeet * 304.8, 1).ToString(CultureInfo.InvariantCulture) +
                             " mm of this crossing (or an earlier crossing of this plan claims the same spot)."
            }));

            result["planned"] = planned;
            result["planned_count"] = planned.Count;
            result["omitted"] = omitted;
            result["coverage"] = planned.Count == 0 ? "none"
                : (omitted.Count == 0 ? "complete" : "partial");
            result["existing_columns_measured"] = existing.Count;
            AttachNextArguments(doc, result, elements);
            return CommandResult.Ok(result);
        }

        // ------------------------------------------------------------------- beams
        private CommandResult PlanBeams(Document doc, JObject request, Level level, FamilySymbol symbol,
                                        List<GridSegment> segments, List<GridIntersection> crossings, JObject result)
        {
            double? requestedMinimum = request.Value<double?>("min_span_mm");
            double minSpanMm = requestedMinimum ?? StructuralLayoutRules.DefaultMinSpanMm;
            if (minSpanMm < 0 || minSpanMm > 20000)
                return CommandResult.Fail("min_span_mm must be between 0 and 20000.");

            double levelZ = LevelGeometryHeight(level);
            List<double> otherLevelZs = OtherLevelHeights(doc, level);
            var allLevelZs = new List<double>(otherLevelZs) { levelZ };
            long levelId = Rid.Value(level.Id);
            var existingFraming = new List<ExistingFramingSegment>();
            var beamBoxes = new List<(double[] Box, long Id, string UniqueId)>();
            var uncertainBeams = new List<(long Id, string UniqueId, BoundingBoxXYZ Bounds)>();
            var unmeasured = new JArray();
            var warnings = new JArray();
            var excludedByBounds = new JArray();
            var placementDefaults = new Dictionary<(BuiltInParameter Parameter, string Default), List<long>>();
            bool usedReferenceLevel = false;
            int usedHeightBand = 0, missingDepth = 0, missingWidth = 0;
            foreach (FamilyInstance framing in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType()
                         .OfType<FamilyInstance>())
            {
                string nonBeamType = null;
                try
                {
                    StructuralType structuralType = framing.StructuralType;
                    if (structuralType != StructuralType.Beam) nonBeamType = structuralType.ToString();
                    long? referenceLevelId = BeamReferenceLevelId(doc, framing);
                    if (referenceLevelId.HasValue)
                    {
                        if (nonBeamType == null) usedReferenceLevel = true;
                        if (referenceLevelId.Value != levelId) continue;
                        if (nonBeamType == null && TryReadModelBounds(framing, out XYZ min, out XYZ max))
                        {
                            if (!StructuralLayoutRules.IntersectsFramingDatumBand(min.Z, max.Z, levelZ)) continue;
                            beamBoxes.Add((new[] { min.X, min.Y, max.X, max.Y }, Rid.Value(framing.Id), framing.UniqueId));
                            continue;
                        }
                    }
                    double depth = referenceLevelId.HasValue ? 0 : BeamDepth(framing);
                    if (!TryReadFramingLine(framing, out XYZ start, out XYZ end, out string reason))
                    {
                        JObject exclusion = referenceLevelId.HasValue ? null
                            : FramingBoundsExclusion(framing, levelId, levelZ, allLevelZs, depth);
                        if (exclusion != null)
                            excludedByBounds.Add(exclusion);
                        else
                            ReportUnreadableFraming(framing, nonBeamType, reason, unmeasured, warnings, uncertainBeams);
                        continue;
                    }

                    if (!referenceLevelId.HasValue)
                    {
                        usedHeightBand++;
                        if (depth == 0) missingDepth++;
                    }
                    if (StructuralLayoutRules.BeamBelongsToLevel(referenceLevelId, levelId, (start.Z + end.Z) / 2,
                                                                 levelZ, otherLevelZs, depth))
                    {
                        double width = BeamWidth(framing);
                        var defaults = new List<(BuiltInParameter Parameter, string Default)>();
                        (start, end, var footprint, var footprintDirection) =
                            ReadFramingPlacement(framing, start, end, width, defaults);
                        if (footprint == null && width == 0) missingWidth++;
                        foreach (var assumption in defaults)
                        {
                            if (!placementDefaults.TryGetValue(assumption, out List<long> ids))
                                placementDefaults.Add(assumption, ids = new List<long>());
                            ids.Add(Rid.Value(framing.Id));
                        }
                        existingFraming.Add(new ExistingFramingSegment(start.X, start.Y, end.X, end.Y, width,
                            Rid.Value(framing.Id), framing.UniqueId, nonBeamType, footprint, footprintDirection));
                    }
                }
                catch (Exception ex)
                {
                    ReportUnreadableFraming(framing, nonBeamType,
                        "Revit could not read the framing for beam coverage: " + ex.Message, unmeasured, warnings, uncertainBeams);
                }
            }

            // Supports can reach the framing band without touching the datum itself.
            var columnBoxes = new List<double[]>();
            foreach (FamilyInstance column in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType()
                         .OfType<FamilyInstance>())
            {
                try
                {
                    if (TryReadModelBounds(column, out XYZ min, out XYZ max) &&
                        StructuralLayoutRules.IntersectsFramingDatumBand(min.Z, max.Z, levelZ))
                        columnBoxes.Add(new[] { min.X, min.Y, max.X, max.Y });
                }
                catch { }
            }

            var elements = new JArray();
            var planned = new JArray();
            var omitted = new JArray();
            var omittedByBeamBox = new JArray();
            var partial = new JArray();
            var uncertainSpans = new JArray();
            // create_elements reads a framing member's end points as ABSOLUTE internal-origin coordinates:
            // the beam sits at the level's own height, not at 0 with the level only as its reference - and that
            // height is ProjectElevation, never Elevation, which may be measured from the shared survey point.
            double levelZmm = StructuralLayoutRules.BeamEndZMm(level.Elevation, level.ProjectElevation);
            int suppressedExisting = 0;
            var gridsById = segments.ToDictionary(segment => segment.ElementId);
            double proposedWidth = beamBoxes.Count == 0 ? 0 : BeamWidth(symbol);
            foreach (GridSegment segment in segments)
            {
                GridBeamPlan plan = StructuralLayoutRules.BeamSpans(crossings, segment.Name, segment.ElementId,
                    existingFraming, minSpanMm / 304.8, columnBoxes, beamBoxes, gridsById, proposedWidth,
                    preserveUnsupportedGaps: !requestedMinimum.HasValue);
                suppressedExisting += plan.SuppressedExisting;
                var withheld = new HashSet<BeamSpan>();
                foreach (BeamSpan span in plan.Planned)
                {
                    JObject row = BeamSpanRow(span, levelZmm);
                    if (uncertainBeams.Count > 0)
                    {
                        var candidates = new JArray(uncertainBeams
                            .Where(beam => MayOverlapSpan(span, beam.Bounds))
                            .Select(beam => new JObject { ["beam_id"] = beam.Id, ["unique_id"] = beam.UniqueId }));
                        if (candidates.Count > 0)
                        {
                            row["code"] = "existing_beam_coverage_unmeasured";
                            row["reason"] = "Existing beam bounds do not establish longitudinal clearance. The proposed family's lateral placement is unmeasured. Check the existing beams' geometry or placement in Revit and run the plan again.";
                            row["unmeasured_beams"] = candidates;
                            uncertainSpans.Add(row);
                            withheld.Add(span);
                            continue;
                        }
                    }
                    planned.Add(row);
                    elements.Add(new JObject
                    {
                        ["kind"] = "structural_framing",
                        ["type_id"] = Rid.Value(symbol.Id),
                        ["level_id"] = Rid.Value(level.Id),
                        ["structural_type"] = "Beam",
                        ["start"] = row["start"].DeepClone(),
                        ["end"] = row["end"].DeepClone()
                    });
                }
                foreach (BeamSpan span in plan.OmittedShort)
                {
                    JObject row = BeamSpanRow(span, levelZmm);
                    row["code"] = StructuralLayoutRules.CodeSpanTooShort;
                    row["reason"] = "the free segment is shorter than the applicable minimum span";
                    omitted.Add(row);
                }
                foreach (var hit in plan.OmittedByBeamBox)
                {
                    JObject row = BeamSpanRow(hit.Span, levelZmm);
                    row["code"] = StructuralLayoutRules.CodeAlreadyPresent;
                    string overlapGrid = hit.CrossingGrid ?? hit.Span.Grid;
                    row["reason"] = $"This part of the bay was omitted because the existing beam on this level would overlap the proposed beam along grid '{overlapGrid}' by more than 150 mm plus the 0.01 mm tolerance.";
                    if (hit.CrossingGrid != null) row["crossing_grid"] = hit.CrossingGrid;
                    row["beam_id"] = hit.BeamId;
                    row["unique_id"] = hit.UniqueId;
                    omittedByBeamBox.Add(row);
                }
                foreach (PartialBeamSpan span in plan.PartiallyCovered)
                {
                    JObject row = BeamSpanRow(span.Bay, levelZmm);
                    row["covered_mm"] = Math.Round(span.CoveredFeet * 304.8, 1);
                    row["covering_beams"] = new JArray(span.CoveringBeams.Select(beam => new JObject
                    {
                        ["beam_id"] = beam.ElementId, ["unique_id"] = beam.UniqueId
                    }));
                    row["planned_free_segments"] = new JArray(span.PlannedFreeSegments
                        .Where(free => !withheld.Contains(free)).Select(free => BeamSpanRow(free, levelZmm)));
                    partial.Add(row);
                }
                foreach (var hit in plan.OtherFramingOnAxis)
                {
                    JObject row = BeamSpanRow(hit.Bay, levelZmm);
                    row["code"] = "non_beam_framing_on_axis";
                    row["reason"] = "existing non-beam framing intersects the lateral allowance of this bay in plan";
                    row["element_id"] = hit.Framing.ElementId;
                    row["unique_id"] = hit.Framing.UniqueId;
                    row["structural_type"] = hit.Framing.NonBeamStructuralType;
                    warnings.Add(row);
                }
            }
            result["planned"] = planned;
            result["planned_count"] = planned.Count;
            result["suppressed_existing_beams"] = suppressedExisting;
            result["suppressed_short_spans"] = omitted.Count;
            result["omitted"] = omitted;
            result["omitted_existing_beams"] = omittedByBeamBox;
            result["partially_covered_spans"] = partial;
            result["coverage"] = uncertainSpans.Count > 0 ? "partial" : planned.Count == 0 ? "none"
                : (suppressedExisting + omitted.Count + unmeasured.Count + partial.Count + warnings.Count == 0 ? "complete" : "partial");
            if (uncertainSpans.Count > 0) result["coverage_unmeasured_spans"] = uncertainSpans;
            if (warnings.Count > 0) result["warnings"] = warnings;
            if (unmeasured.Count > 0) result["unmeasured_existing_beams"] = unmeasured;
            if (usedReferenceLevel)
                AssumptionNotes.Stamp(result, new[] { AssumptionNotes.Row(
                    "A readable reference level decides which level an existing beam belongs to.",
                    "An offset can place the beam's location line at the elevation of another level.",
                    "Correct the beam's Reference Level in Revit if it should belong to a different level, then run the plan again.") });
            if (usedHeightBand > 0)
                AssumptionNotes.Stamp(result, new[] { AssumptionNotes.Row(
                    $"Checked {usedHeightBand} framing member(s) against the target level by location-line height.",
                    "Their reference level could not be read. The height band allows for the section depth below the level " +
                    $"when known. For {missingDepth} member(s) with no readable depth, the standard band was used.",
                    "Set the framing members' Reference Level in Revit and run the plan again.") });
            if (excludedByBounds.Count > 0)
            {
                result["framing_excluded_by_bounds"] = excludedByBounds;
                AssumptionNotes.Stamp(result, new[] { AssumptionNotes.Row(
                    $"Excluded {excludedByBounds.Count} framing member(s) from the target level by bounding-box height.",
                    "Their reference level and location line could not be read. The entire box was outside the target " +
                    "height band and fit the band of another known level, allowing for section depth when readable.",
                    "Set the framing members' Reference Level in Revit and run the plan again.") });
            }
            if (missingWidth > 0)
                AssumptionNotes.Stamp(result, new[] { AssumptionNotes.Row(
                    "Used a lateral allowance of " +
                    (StructuralLayoutRules.UnknownBeamWidthToleranceFeet * 304.8).ToString("0.#", CultureInfo.InvariantCulture) +
                    $" mm from the adjusted axis for {missingWidth} framing member(s) on the target level.",
                    "Their type had no readable positive section width.",
                    "Set the section width (b, Width or Ancho) on the existing framing types and run the plan again.") });
            if (placementDefaults.Count > 0)
                AssumptionNotes.Stamp(result, placementDefaults.Select(pair => AssumptionNotes.Row(
                    $"Used {pair.Key.Default} for {pair.Key.Parameter} on framing IDs {string.Join(", ", pair.Value)}.",
                    "The placement parameter was missing, unreadable or had no supported value.",
                    $"Set {pair.Key.Parameter} explicitly in the framing instance's y/z justification and y offset properties in Revit, then run the plan again.")));
            AttachNextArguments(doc, result, elements);
            return CommandResult.Ok(result);
        }

        private static JObject BeamSpanRow(BeamSpan span, double levelZmm) => new JObject
        {
            ["grid"] = span.Grid, ["grid_id"] = span.GridId,
            ["from"] = span.FromCrossing, ["to"] = span.ToCrossing,
            ["start"] = new JArray(span.X1 * 304.8, span.Y1 * 304.8, levelZmm),
            ["end"] = new JArray(span.X2 * 304.8, span.Y2 * 304.8, levelZmm),
            ["length_mm"] = Math.Round(Math.Sqrt(
                Math.Pow(span.X2 - span.X1, 2) + Math.Pow(span.Y2 - span.Y1, 2)) * 304.8, 1)
        };

        private static bool TryReadFramingLine(FamilyInstance framing, out XYZ start, out XYZ end, out string reason)
        {
            start = end = null;
            reason = "the framing has no readable straight, bounded location line";
            try
            {
                if (!((framing.Location as LocationCurve)?.Curve is Line line) || !line.IsBound) return false;
                start = line.GetEndPoint(0);
                end = line.GetEndPoint(1);
                return true;
            }
            catch (Exception ex)
            {
                reason += ": " + ex.Message;
                return false;
            }
        }

        private static bool TryReadModelBounds(Element element, out XYZ min, out XYZ max)
        {
            min = max = null;
            try
            {
                BoundingBoxXYZ box = element.get_BoundingBox(null);
                min = box?.Min;
                max = box?.Max;
                return min != null && max != null &&
                       Valid(min.X, max.X) && Valid(min.Y, max.Y) && Valid(min.Z, max.Z);
            }
            catch { return false; }

            static bool Valid(double lo, double hi) => lo <= hi && !double.IsInfinity(lo) && !double.IsInfinity(hi);
        }

        private static (XYZ Start, XYZ End, IList<FramingPlanTriangle> Footprint, FramingFootprintDirection Direction) ReadFramingPlacement(
            FamilyInstance framing, XYZ start, XYZ end, double width, List<(BuiltInParameter, string)> defaults)
        {
            double dx = end.X - start.X, dy = end.Y - start.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 1e-9) return (start, end, null, FramingFootprintDirection.Unfiltered);

            // Revit measures positive y offsets to the right looking from start to end.
            // Use the original direction at both ends, even for independent offsets.
            double rightX = dy / length, rightY = -dx / length;
            var mode = (YZJustificationOption?)TryReadFramingInteger(framing, BuiltInParameter.YZ_JUSTIFICATION);
            if (mode != YZJustificationOption.Uniform && mode != YZJustificationOption.Independent)
            {
                var footprint = ReadFramingFootprint(framing, "unreadable YZ_JUSTIFICATION");
                defaults.Add((BuiltInParameter.YZ_JUSTIFICATION,
                    "the measured footprint; skip crossings only when its projected extents and the reference line both indicate a transverse beam, allowing for any known section width"));
                return (start, end, footprint, FramingFootprintDirection.MeasuredExtents);
            }
            // A rotated profile's width and lateral center are properties of its placed geometry.
            // A uniform lateral offset preserves the line's direction. Independent placement can turn it.
            if (FramingParameterMayChangeGeometry(framing, BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE))
                return (start, end, ReadFramingFootprint(framing, "STRUCTURAL_BEND_DIR_ANGLE"),
                    mode == YZJustificationOption.Uniform ? FramingFootprintDirection.PlacementAxis
                        : FramingFootprintDirection.MeasuredExtents);

            BuiltInParameter startJustificationId, endJustificationId, startOffsetId, endOffsetId;
            switch (mode)
            {
                case YZJustificationOption.Uniform:
                    startJustificationId = endJustificationId = BuiltInParameter.Y_JUSTIFICATION;
                    startOffsetId = endOffsetId = BuiltInParameter.Y_OFFSET_VALUE;
                    break;
                case YZJustificationOption.Independent:
                    startJustificationId = BuiltInParameter.START_Y_JUSTIFICATION;
                    endJustificationId = BuiltInParameter.END_Y_JUSTIFICATION;
                    startOffsetId = BuiltInParameter.START_Y_OFFSET_VALUE;
                    endOffsetId = BuiltInParameter.END_Y_OFFSET_VALUE;
                    break;
                default:
                    throw new InvalidOperationException("Unknown yz justification mode: " + mode + ".");
            }
            var startJustification = Justification(startJustificationId);
            var endJustification = mode == YZJustificationOption.Uniform ? startJustification : Justification(endJustificationId);
            if (startJustification == YJustification.Origin || endJustification == YJustification.Origin)
            {
                // Origin aligns the family datum, which can lie entirely outside the section.
                // The footprint already includes both end placements, offsets and section rotation.
                var footprint = ReadFramingFootprint(framing, "Origin y justification");
                if (mode == YZJustificationOption.Uniform)
                    return (start, end, footprint, FramingFootprintDirection.PlacementAxis);
                if (startJustification == YJustification.Origin && endJustification == YJustification.Origin)
                {
                    // The same family datum at both ends cancels out of the direction;
                    // only the difference between the two lateral offsets turns the axis.
                    double? startOffset = OriginOffset(startOffsetId);
                    double? endOffset = OriginOffset(endOffsetId);
                    if (startOffset.HasValue && endOffset.HasValue)
                        return (start, MoveRight(end, endOffset.Value - startOffset.Value), footprint,
                            FramingFootprintDirection.PlacementAxis);
                }
                return (start, end, footprint, FramingFootprintDirection.Unfiltered);
            }
            double startShift = FramingRightShift(framing, width, startJustification, startOffsetId, defaults);
            double endShift = mode == YZJustificationOption.Uniform ? startShift
                : FramingRightShift(framing, width, endJustification, endOffsetId, defaults);
            XYZ adjustedStart = MoveRight(start, startShift);
            XYZ adjustedEnd = MoveRight(end, endShift);
            // The location curve ends at the handles. Revit can extend or trim the solid beyond them,
            // independently of lateral justification. Read the solid rather than extrapolating the parameters.
            BuiltInParameter? endAdjustment = FramingEndAdjustment(framing);
            if (endAdjustment.HasValue)
                return (adjustedStart, adjustedEnd, ReadFramingFootprint(framing, endAdjustment.Value.ToString()),
                    FramingFootprintDirection.PlacementAxis);
            return (adjustedStart, adjustedEnd, null, FramingFootprintDirection.Unfiltered);

            YJustification Justification(BuiltInParameter id) => (YJustification)ReadFramingInteger(
                framing, id, (int)YJustification.Center, "Center (no lateral shift from justification)", defaults);
            XYZ MoveRight(XYZ point, double shift) =>
                new XYZ(point.X + rightX * shift, point.Y + rightY * shift, point.Z);
            double? OriginOffset(BuiltInParameter id)
            {
                try { return FramingRightOffset(framing, id, defaults); }
                catch
                {
                    defaults.Add((id, "the measured Origin footprint without a direction filter"));
                    return null;
                }
            }
        }

        private static readonly BuiltInParameter[] FramingEndParameters =
        {
            BuiltInParameter.START_EXTENSION, BuiltInParameter.END_EXTENSION,
            BuiltInParameter.START_JOIN_CUTBACK, BuiltInParameter.END_JOIN_CUTBACK
        };

        private static BuiltInParameter? FramingEndAdjustment(FamilyInstance framing)
        {
            foreach (BuiltInParameter id in FramingEndParameters)
                if (FramingParameterMayChangeGeometry(framing, id)) return id;
            return null;
        }

        private static bool FramingParameterMayChangeGeometry(FamilyInstance framing, BuiltInParameter id)
        {
            try
            {
                Parameter parameter = framing.get_Parameter(id);
                // An inapplicable parameter can be absent. A present but unreadable one
                // cannot establish that the nominal section and location line are safe.
                return parameter != null &&
                    (!parameter.HasValue || parameter.StorageType != StorageType.Double || parameter.AsDouble() != 0);
            }
            catch { return true; }
        }

        private static double FramingRightShift(FamilyInstance framing, double width, YJustification justification,
                                                BuiltInParameter offsetId, List<(BuiltInParameter, string)> defaults)
        {
            double offset = FramingRightOffset(framing, offsetId, defaults);
            switch (justification)
            {
                case YJustification.Center:
                    return offset;
                case YJustification.Left:
                case YJustification.Right:
                    if (width <= 0) throw new InvalidOperationException("Cannot read section width for side justification.");
                    return offset + (justification == YJustification.Left ? width / 2 : -width / 2);
                default:
                    throw new InvalidOperationException("Unknown y justification: " + justification + ".");
            }
        }

        private static double FramingRightOffset(FamilyInstance framing, BuiltInParameter id,
                                                 List<(BuiltInParameter, string)> defaults)
        {
            Parameter parameter = framing.get_Parameter(id);
            if (parameter == null || !parameter.HasValue)
            {
                if (id != BuiltInParameter.Y_OFFSET_VALUE)
                    throw new InvalidOperationException("Cannot read framing offset " + id + ".");
                defaults.Add((id, "0 mm"));
                return 0;
            }
            if (parameter.StorageType != StorageType.Double)
                throw new InvalidOperationException("Cannot read framing offset " + id + ".");
            double offset = parameter.AsDouble();
            if (double.IsNaN(offset) || double.IsInfinity(offset))
                throw new InvalidOperationException("Invalid framing offset " + id + ".");
            return offset;
        }

        private static int? TryReadFramingInteger(FamilyInstance framing, BuiltInParameter id)
        {
            try
            {
                Parameter parameter = framing.get_Parameter(id);
                return parameter != null && parameter.HasValue && parameter.StorageType == StorageType.Integer
                    ? (int?)parameter.AsInteger() : null;
            }
            catch { return null; }
        }

        private static int ReadFramingInteger(FamilyInstance framing, BuiltInParameter id, int defaultValue,
                                              string defaultName, List<(BuiltInParameter, string)> defaults)
        {
            Parameter parameter = framing.get_Parameter(id);
            if (parameter == null || !parameter.HasValue)
            {
                defaults.Add((id, defaultName));
                return defaultValue;
            }
            if (parameter.StorageType != StorageType.Integer)
                throw new InvalidOperationException("Cannot read framing justification " + id + ".");
            return parameter.AsInteger();
        }

        private static List<FramingPlanTriangle> ReadFramingFootprint(FamilyInstance framing, string placement)
        {
            var footprint = new List<FramingPlanTriangle>();
            Read(framing.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }));
            if (footprint.Count == 0)
                throw new InvalidOperationException("No measurable model geometry for " + placement + ".");
            return footprint;

            void Read(GeometryElement geometry)
            {
                if (geometry == null) return;
                foreach (GeometryObject item in geometry)
                {
                    if (item is GeometryInstance instance) Read(instance.GetInstanceGeometry());
                    else if (item is Solid solid)
                        foreach (Face face in solid.Faces) ReadMesh(face.Triangulate());
                    else if (item is Mesh mesh) ReadMesh(mesh);
                }
            }

            void ReadMesh(Mesh mesh)
            {
                for (int i = 0; i < mesh.NumTriangles; i++)
                {
                    MeshTriangle triangle = mesh.get_Triangle(i);
                    XYZ a = triangle.get_Vertex(0), b = triangle.get_Vertex(1), c = triangle.get_Vertex(2);
                    var projected = new FramingPlanTriangle(a.X, a.Y, b.X, b.Y, c.X, c.Y);
                    if (projected.HasArea) footprint.Add(projected);
                }
            }
        }

        private static JObject FramingBoundsExclusion(FamilyInstance framing, long levelId, double levelZ,
                                                       List<double> allLevelZs, double depth)
        {
            try
            {
                BoundingBoxXYZ box = framing.get_BoundingBox(null);
                if (box == null) return null;
                // Element bounding boxes in model coordinates are aligned to the model axes.
                // Keep uncertainty if any part of the box could still lie within the target band.
                double minZ = box.Min.Z, maxZ = box.Max.Z;
                if (double.IsNaN(minZ) || double.IsInfinity(minZ) || double.IsNaN(maxZ) ||
                    double.IsInfinity(maxZ) || minZ > maxZ) return null;
                double nearestZ = Math.Max(minZ, Math.Min(levelZ, maxZ));
                if (StructuralLayoutRules.BeamBelongsToLevel(null, levelId, nearestZ, levelZ, allLevelZs, depth))
                    return null;

                foreach (double otherZ in allLevelZs)
                {
                    if (Math.Abs(otherZ - levelZ) <= 1e-9 ||
                        !StructuralLayoutRules.BeamBelongsToLevel(null, levelId, minZ, otherZ, allLevelZs, depth) ||
                        !StructuralLayoutRules.BeamBelongsToLevel(null, levelId, maxZ, otherZ, allLevelZs, depth)) continue;
                    return new JObject
                    {
                        ["element_id"] = Rid.Value(framing.Id),
                        ["unique_id"] = framing.UniqueId,
                        ["min_z_mm"] = minZ * 304.8,
                        ["max_z_mm"] = maxZ * 304.8,
                        ["target_level_z_mm"] = levelZ * 304.8,
                        ["matched_level_z_mm"] = otherZ * 304.8,
                        ["section_depth_mm"] = depth > 0 ? new JValue(depth * 304.8) : JValue.CreateNull()
                    };
                }
                return null;
            }
            catch { return null; } // An unreadable box cannot establish another level.
        }

        private static void ReportUnreadableFraming(FamilyInstance framing, string nonBeamType, string reason,
            JArray unmeasured, JArray warnings, List<(long, string, BoundingBoxXYZ)> uncertainBeams)
        {
            if (nonBeamType == null)
            {
                long id = Rid.Value(framing.Id);
                string uniqueId = framing.UniqueId;
                unmeasured.Add(new JObject { ["beam_id"] = id, ["unique_id"] = uniqueId, ["reason"] = reason });
                BoundingBoxXYZ bounds = null;
                try { bounds = framing.get_BoundingBox(null); }
                catch { } // With no model bounds, no candidate span can be cleared by position.
                uncertainBeams.Add((id, uniqueId, bounds));
            }
            else
                warnings.Add(new JObject
                {
                    ["code"] = "non_beam_framing_axis_unmeasured",
                    ["element_id"] = Rid.Value(framing.Id), ["unique_id"] = framing.UniqueId,
                    ["structural_type"] = nonBeamType, ["reason"] = reason
                });
        }

        private static bool MayOverlapSpan(BeamSpan span, BoundingBoxXYZ bounds)
        {
            // get_BoundingBox(null) supplies model-axis-aligned bounds.
            XYZ min = bounds?.Min, max = bounds?.Max;
            return min == null || max == null ||
                   StructuralLayoutRules.BoundsMayOverlapBeamLongitudinally(span, min.X, min.Y, max.X, max.Y);
        }

        private static long? BeamReferenceLevelId(Document doc, FamilyInstance beam)
        {
            try
            {
                ElementId id = beam.LevelId;
                if (id != null && id != ElementId.InvalidElementId && doc.GetElement(id) is Level) return Rid.Value(id);
            }
            catch { }
            try
            {
                Parameter reference = beam.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                if (reference == null || !reference.HasValue || reference.StorageType != StorageType.ElementId) return null;
                ElementId id = reference.AsElementId();
                if (id != null && id != ElementId.InvalidElementId && doc.GetElement(id) is Level) return Rid.Value(id);
            }
            catch { }
            return null;
        }

        private static double BeamDepth(FamilyInstance beam)
        {
            foreach (BuiltInParameter id in new[] { BuiltInParameter.STRUCTURAL_SECTION_COMMON_HEIGHT,
                                                    BuiltInParameter.FAMILY_HEIGHT_PARAM })
            {
                double depth = PositiveLength(beam, id);
                if (depth > 0) return depth;
                depth = PositiveLength(beam.Symbol, id);
                if (depth > 0) return depth;
            }
            return 0;
        }

        private static double BeamWidth(FamilyInstance beam)
        {
            try { return BeamWidth(beam.Symbol); }
            catch { return 0; }
        }

        private static double BeamWidth(FamilySymbol symbol)
        {
            if (symbol == null) return 0;

            foreach (BuiltInParameter id in new[] { BuiltInParameter.STRUCTURAL_SECTION_COMMON_WIDTH,
                                                    BuiltInParameter.FAMILY_WIDTH_PARAM })
            {
                double width = PositiveLength(symbol, id);
                if (width > 0) return width;
            }
            foreach (string name in new[] { "b", "B", "Ancho", "ancho", "Width", "width" })
            {
                try
                {
                    foreach (Parameter parameter in symbol.GetParameters(name))
                    {
                        if (parameter.Definition.GetDataType() != SpecTypeId.Length) continue;
                        double width = PositiveLength(parameter);
                        if (width > 0) return width;
                    }
                }
                catch { } // Some types do not expose a readable length under this name.
            }
            return 0;
        }

        private static double PositiveLength(Element element, BuiltInParameter id)
        {
            try { return PositiveLength(element?.get_Parameter(id)); }
            catch { return 0; }
        }

        private static double PositiveLength(Parameter parameter)
        {
            if (parameter != null && parameter.HasValue && parameter.StorageType == StorageType.Double)
            {
                double value = parameter.AsDouble();
                if (value > 0 && !double.IsInfinity(value)) return value;
            }
            return 0;
        }

        // ------------------------------------------------------------------ shared
        private static string CollectGrids(Document doc, JObject request, out List<GridSegment> segments,
                                           JArray unplannable)
        {
            segments = new List<GridSegment>();
            var wanted = new HashSet<long>();
            if (request["grid_ids"] is JArray ids)
                foreach (JToken token in ids)
                {
                    long raw = token.Value<long?>() ?? -1;
                    if (!Rid.CanRepresent(raw)) return "grid_ids contains a value outside ElementId range.";
                    wanted.Add(raw);
                }
            foreach (Grid grid in new FilteredElementCollector(doc).OfClass(typeof(Grid)).OfType<Grid>())
            {
                long id = Rid.Value(grid.Id);
                if (wanted.Count > 0 && !wanted.Contains(id)) continue;
                Curve curve = null;
                try { curve = grid.Curve; } catch { }
                if (curve is Line line)
                {
                    XYZ start = line.GetEndPoint(0), end = line.GetEndPoint(1);
                    segments.Add(new GridSegment
                    {
                        Name = SafeName(grid), ElementId = id,
                        X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y
                    });
                }
                else
                {
                    unplannable.Add(new JObject
                    {
                        ["grid_id"] = id, ["name"] = SafeName(grid),
                        ["reason"] = curve == null ? "its curve could not be read"
                            : "it is not a straight line; arc grids are not planned - name straight grids explicitly"
                    });
                }
            }
            if (wanted.Count > 0 && segments.Count + unplannable.Count < wanted.Count)
                return "grid_ids named " + wanted.Count + " grid(s) but only " + (segments.Count + unplannable.Count) +
                       " resolved as grids in this document.";
            return null;
        }

        private static void AttachNextArguments(Document doc, JObject result, JArray elements)
        {
            if (elements.Count == 0) return;
            const int limit = Horizun.Contracts.Contract.CreateElementsBatchLimit;
            if (elements.Count <= limit)
            {
                result["next_arguments"] = CreationRequest(doc.Title, elements);
                return;
            }

            var batches = new JArray();
            for (int offset = 0; offset < elements.Count; offset += limit)
                batches.Add(CreationRequest(doc.Title, new JArray(elements.Skip(offset).Take(limit))));
            result["next_arguments_batches"] = batches;
        }

        private static JObject CreationRequest(string documentTitle, JArray elements) => new JObject
        {
            ["tool"] = "horizun_create_elements",
            ["arguments"] = new JObject
            {
                ["target_document"] = documentTitle,
                ["units"] = "mm",
                ["elements"] = elements
            }
        };

        private static string SafeName(Element element)
        {
            try { return element?.Name; } catch { return null; }
        }
    }
}
