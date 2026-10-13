// -----------------------------------------------------------------------------
// Horizun Revit MCP - ONE reader for what a rebar is.
// Original Horizun code.
//
// WHY THERE IS ONLY ONE.
//
// Three different parts of this bridge need to say what a bar is: the query that
// reports it, the apply that verifies what it just wrote, and the audit that
// compares it against a requirement set. If each read the model its own way,
// they could disagree - and the failure would be silent and specific: the apply
// would confirm a set the audit then reports as wrong, in the same model, in the
// same minute, because one of them counted bar POSITIONS and the other counted
// BARS.
//
// That is not hypothetical. Revit publishes both numbers and they differ
// whenever an end bar is suppressed. Quantity is bars. NumberOfBarPositions is
// array slots. This file is where that distinction is made once.
//
// MEASURED, not derived. Total length, volume and quantity are read off the
// element, never recomputed from the layout this bridge thinks it created - the
// difference between a quantity and an assertion.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public static class RebarFacts
    {
        public const double FtToMm = 304.8;

        /// <summary>Every fact this bridge can state about one bar set, with its own coverage.</summary>
        public static JObject Describe(Document doc, Rebar bar, bool includePositions)
        {
            var reasons = new JArray();
            var o = new JObject
            {
                ["id"] = Rid.Value(bar.Id),
                ["unique_id"] = Safe(() => bar.UniqueId),
                ["name"] = Safe(() => bar.Name)
            };

            // ------------------------------------------------------------ host
            ElementId hostId = null;
            try { hostId = bar.GetHostId(); } catch { }
            Element host = (hostId != null && hostId != ElementId.InvalidElementId) ? doc.GetElement(hostId) : null;
            o["host"] = host == null
                ? (JToken)new JObject
                {
                    ["id"] = hostId == null ? -1 : Rid.Value(hostId),
                    ["resolved"] = false,
                    // A bar whose host cannot be resolved is a finding, not a blank.
                    ["why"] = hostId == null || hostId == ElementId.InvalidElementId
                        ? "the bar reports no host id."
                        : "the bar names a host id that is not in this document."
                }
                : new JObject
                {
                    ["id"] = Rid.Value(host.Id),
                    ["resolved"] = true,
                    ["category"] = host.Category == null ? null : host.Category.Name,
                    ["name"] = Safe(() => host.Name)
                };

            // -------------------------------------------------------- bar type
            var typeBlock = new JObject();
            RebarBarType barType = doc.GetElement(bar.GetTypeId()) as RebarBarType;
            if (barType == null)
            {
                typeBlock["resolved"] = false;
                reasons.Add(StructuralCoverage.Reason("bar_type", "the bar type did not resolve to a RebarBarType."));
            }
            else
            {
                typeBlock["resolved"] = true;
                typeBlock["id"] = Rid.Value(barType.Id);
                typeBlock["name"] = Safe(() => barType.Name);
                // BOTH diameters, always, and never one under a name that does not
                // say which. They are different numbers and a bar schedule built on
                // the wrong one is wrong by the rib height on every bar.
                typeBlock["nominal_diameter_mm"] = Round(Safe(() => barType.BarNominalDiameter) * FtToMm);
                typeBlock["model_diameter_mm"] = Round(Safe(() => barType.BarModelDiameter) * FtToMm);
                typeBlock["standard_bend_diameter_mm"] = Round(Safe(() => barType.StandardBendDiameter) * FtToMm);
                typeBlock["stirrup_tie_bend_diameter_mm"] = Round(Safe(() => barType.StirrupTieBendDiameter) * FtToMm);
                typeBlock["bend_diameter_means"] =
                    "how big a radius Revit puts in a corner. A declared centreline draws its corners sharp, " +
                    "so the point-by-point comparison allows for this rather than for a loose tolerance.";
                typeBlock["diameter_means"] =
                    "nominal is the designation diameter a schedule and a lap length use; model is what Revit " +
                    "draws and what a clash measures. They are not the same number and neither is 'the diameter'.";
            }
            o["bar_type"] = typeBlock;

            // ----------------------------------------------------------- shape
            ElementId shapeId = null;
            try { shapeId = bar.GetShapeId(); } catch { }
            RebarShape shape = (shapeId != null && shapeId != ElementId.InvalidElementId)
                ? doc.GetElement(shapeId) as RebarShape : null;
            o["shape"] = new JObject
            {
                ["id"] = shapeId == null ? -1 : Rid.Value(shapeId),
                ["name"] = shape == null ? null : Safe(() => shape.Name),
                ["resolved"] = shape != null
            };

            // THE STYLE, which lives on the SHAPE and nowhere on the bar. It is
            // published because CreateFromCurvesAndShape takes the style from the
            // shape and ignores everything else, so without this the audit has
            // nothing to compare a declared style against.
            string style = null;
            if (shape != null)
            {
                try
                {
                    style = shape.RebarStyle == RebarStyle.StirrupTie
                        ? StructuralStyle.StirrupTie : StructuralStyle.Standard;
                }
                catch { }
            }
            o["style_horizun"] = style;
            if (style == null)
                reasons.Add(StructuralCoverage.Reason("style",
                    shape == null
                        ? "this bar has no shape, so it carries no style to read - a free-form bar is neither."
                        : "the shape would not report its style.", Rid.Value(bar.Id)));

            bool shapeDriven = false;
            try { shapeDriven = bar.IsRebarShapeDriven(); } catch { }
            o["is_shape_driven"] = shapeDriven;
            o["is_free_form"] = Safe(() => bar.IsRebarFreeForm());

            // ---------------------------------------------------------- layout
            XYZ barNormal = null;
            var layout = new JObject();
            layout["rule"] = Safe(() => bar.LayoutRule.ToString());
            layout["rule_horizun"] = LayoutWord(Safe(() => bar.LayoutRule.ToString()));
            // THE TWO NUMBERS THAT ARE NOT THE SAME NUMBER.
            layout["number_of_bar_positions"] = Safe(() => (int?)bar.NumberOfBarPositions);
            layout["quantity"] = Safe(() => (int?)bar.Quantity);
            layout["counts_mean"] =
                "number_of_bar_positions is array SLOTS; quantity is BARS actually standing. They differ " +
                "whenever an end bar is excluded or a bar in the set was individually removed.";
            layout["include_first_bar"] = Safe(() => (bool?)bar.IncludeFirstBar);
            layout["include_last_bar"] = Safe(() => (bool?)bar.IncludeLastBar);
            layout["max_spacing_mm"] = Round(Safe(() => bar.MaxSpacing) * FtToMm);

            RebarShapeDrivenAccessor acc = null;
            if (shapeDriven) { try { acc = bar.GetShapeDrivenAccessor(); } catch { } }
            if (acc == null)
            {
                layout["array_length_mm"] = null;
                layout["bars_on_normal_side"] = null;
                layout["normal"] = null;
                layout["accessor"] = shapeDriven ? "unreadable" : "not_applicable";
                if (shapeDriven)
                    reasons.Add(StructuralCoverage.Reason("shape_driven_accessor",
                        "the bar says it is shape driven and would not return its accessor.", Rid.Value(bar.Id)));
            }
            else
            {
                layout["accessor"] = "read";
                layout["array_length_mm"] = Round(Safe(() => acc.ArrayLength) * FtToMm);
                layout["bars_on_normal_side"] = Safe(() => (bool?)acc.BarsOnNormalSide);
                XYZ n = null;
                try { n = acc.Normal; } catch { }
                layout["normal"] = n == null ? null : Xyz(n, 6);
                barNormal = n;
                Line path = null;
                try { path = acc.GetDistributionPath(); } catch { }
                layout["distribution_path"] = path == null ? null : new JObject
                {
                    ["start_mm"] = Xyz(path.GetEndPoint(0), 3, FtToMm),
                    ["end_mm"] = Xyz(path.GetEndPoint(1), 3, FtToMm),
                    ["length_mm"] = Round(path.Length * FtToMm)
                };
            }
            o["layout"] = layout;

            // ---------------------------------------------------- terminations
            var terms = new JArray();
            // THE SIDE EACH HOOK IS DRAWN ON (etapa 1b): Revit's orientation word can disagree with its drawing
            // (measured, Revit 2024: a straight bar matched to M_01 drew 'left' and read 'right').
            List<double[]> drawnPts = null, declaredPts = null;
            for (int end = 0; end <= 1; end++)
            {
                ElementId hookId = null;
                bool hookReadable = true;
                // -1 MEANT TWO THINGS: no hook, and a hook that could not be read.
                // The audit compared the number and therefore called an unreadable
                // hook an agreeing one whenever the rule declared none.
                try { hookId = bar.GetHookTypeId(end); }
                catch { hookReadable = false; }
                Element hook = (hookId != null && hookId != ElementId.InvalidElementId) ? doc.GetElement(hookId) : null;
                // Orientation goes through the shim, because 2027 removed the enum
                // this used to be read with.
                string orientation = RebarApi.ReadOrientation(bar, end);
                if (orientation == null)
                    reasons.Add(StructuralCoverage.Reason("termination_orientation",
                        "the bar would not report the orientation at end " + end + ".", Rid.Value(bar.Id)));
                ElementId couplerId = null;
                // -1 MEANT TWO THINGS here too (L7 audits couplers): none, and could not read.
                bool couplerReadable = true;
                try { couplerId = bar.GetCouplerId(end); } catch { couplerReadable = false; }
                // THE COUPLER'S BODY (L7): where a coupled end may sit - Revit leaves a gap inside it.
                JObject couplerBox = null;
                if (couplerId != null && couplerId != ElementId.InvalidElementId)
                {
                    try
                    {
                        BoundingBoxXYZ cb = bar.Document.GetElement(couplerId)?.get_BoundingBox(null);
                        if (cb != null)
                            couplerBox = new JObject
                            {
                                ["min"] = new JArray(Round(cb.Min.X * FtToMm), Round(cb.Min.Y * FtToMm), Round(cb.Min.Z * FtToMm)),
                                ["max"] = new JArray(Round(cb.Max.X * FtToMm), Round(cb.Max.Y * FtToMm), Round(cb.Max.Z * FtToMm))
                            };
                    }
                    catch { }
                }
                terms.Add(new JObject
                {
                    ["coupler_box_mm"] = couplerBox,
                    ["end"] = end,
                    ["hook_readable"] = hookReadable,
                    ["hook_type_id"] = hookId == null ? -1 : Rid.Value(hookId),
                    ["hook_type_name"] = hook == null ? null : Safe(() => hook.Name),
                    ["has_hook"] = hookId != null && hookId != ElementId.InvalidElementId,
                    ["orientation"] = orientation,
                    ["drawn_orientation"] = DrawnSide(bar, barNormal, end, hookId, ref drawnPts, ref declaredPts),
                    ["coupler_id"] = (couplerId == null || couplerId == ElementId.InvalidElementId)
                        ? -1 : Rid.Value(couplerId),
                    ["coupler_readable"] = couplerReadable
                });
            }
            o["terminations"] = terms;
            o["api_generation"] = RebarApi.ApiGeneration;

            // -------------------------------------------------------- measured
            // Read off the element. NOT recomputed from the layout this bridge
            // believes it created, which would only ever confirm its own opinion.
            var measured = new JObject();
            double? totalLengthFt = Safe(() => bar.TotalLength);
            double? volumeFt3 = Safe(() => bar.Volume);
            measured["total_length_mm"] = Round(totalLengthFt * FtToMm);
            measured["total_length_m"] = Round(totalLengthFt.HasValue ? (double?)Guard.ToM(totalLengthFt.Value) : null, 4);
            measured["volume_m3"] = Round(volumeFt3.HasValue ? (double?)Guard.ToM3(volumeFt3.Value) : null, 6);
            measured["quantity"] = Safe(() => (int?)bar.Quantity);
            measured["schedule_mark"] = Safe(() => bar.ScheduleMark);
            // ONE BAR'S CENTRELINE LENGTH, EXACT (L2). TotalLength is ROUNDED by the bar type's
            // rounding rule (measured: 3 x 3770 against 3 x 3767.85), and the rounding value the
            // API reports is in display units, not internal ones - so the exact length is measured
            // off the curves instead, and the audit compares against it.
            double? oneBarFt = OneBarCentrelineFt(bar);
            measured["bar_centreline_length_mm"] = Round(oneBarFt * FtToMm);
            measured["source"] = "read from the element, not derived from the plan";
            if (!totalLengthFt.HasValue)
                reasons.Add(StructuralCoverage.Reason("total_length", "the bar would not report its total length.",
                                                     Rid.Value(bar.Id)));
            o["measured"] = measured;

            // -------------------------------------------------------- geometry
            o["geometry"] = Geometry(bar, reasons);

            // --------------------------------------------------- bar positions
            if (includePositions)
            {
                var positions = new JArray();
                int slots = Safe(() => (int?)bar.NumberOfBarPositions) ?? 0;
                bool positionsReadable = acc != null;
                var offsets = new List<XYZ>();
                for (int i = 0; i < slots; i++)
                {
                    var row = new JObject { ["index"] = i };
                    bool? exists = null;
                    try { exists = bar.DoesBarExistAtPosition(i); } catch { }
                    row["exists"] = exists;
                    if (positionsReadable)
                    {
                        Transform t = null;
                        try { t = acc.GetBarPositionTransform(i); } catch { }
                        // AN OFFSET, NOT A MODEL POINT. MEASURED on Revit 2026: every
                        // set's position 0 comes back at (0, 0, 0) and the rest are
                        // displacements along the distribution direction - negative
                        // ones when the set marches to the other side. This was
                        // published as `origin_mm` beside a set of model coordinates,
                        // which is a different quantity by the whole position of the
                        // bar in the building.
                        row["offset_from_first_bar_mm"] = t == null ? null : Xyz(t.Origin, 3, FtToMm);
                        if (t == null) positionsReadable = false;
                        else offsets.Add(t.Origin);
                    }
                    else row["offset_from_first_bar_mm"] = null;
                    positions.Add(row);
                }
                o["bar_positions"] = positions;
                o["bar_positions_mean"] =
                    "offsets from the FIRST bar of the set, in millimetres, as Revit computes them. Position 0 " +
                    "is always (0,0,0) and the offsets run negative when the set marches to the other side of " +
                    "the bar. They are not model coordinates.";

                // THE PITCH, MEASURED. Rebar.MaxSpacing is the value that was
                // DECLARED to the layout, not the pitch the bars ended up at -
                // measured on Revit 2026, maximum_spacing of 300 over a 1000 mm array
                // reports 300 and lays the bars at 250. The distance between
                // consecutive positions is the pitch, and it is the only source for
                // it that cannot disagree with the model.
                double? pitch = null;
                bool uniform = true;
                if (offsets.Count >= 2)
                {
                    pitch = offsets[0].DistanceTo(offsets[1]) * FtToMm;
                    for (int i = 2; i < offsets.Count; i++)
                    {
                        double gap = offsets[i - 1].DistanceTo(offsets[i]) * FtToMm;
                        if (Math.Abs(gap - pitch.Value) > 0.01) uniform = false;
                    }
                }
                ((JObject)o["layout"])["measured_pitch_mm"] = Round(pitch);
                ((JObject)o["layout"])["measured_pitch_uniform"] = offsets.Count >= 2 ? (JToken)uniform : JValue.CreateNull();
                ((JObject)o["layout"])["measured_pitch_means"] =
                    "the distance between consecutive bar positions, measured from the transforms Revit " +
                    "computed. NOT Rebar.MaxSpacing, which is the number the layout was given: for " +
                    "maximum_spacing and minimum_clear_spacing those two are different quantities.";

                if (slots > 0 && !positionsReadable)
                    reasons.Add(StructuralCoverage.Reason("bar_positions",
                        "the transform of at least one bar position could not be read, so the set was not " +
                        "proved to sit inside its host.", Rid.Value(bar.Id)));
            }

            // -------------------------------------------------------- coverage
            string coverage = reasons.Count == 0 ? StructuralCoverage.Complete : StructuralCoverage.Partial;
            o["coverage"] = StructuralCoverage.Declare(coverage, 1, reasons.Count == 0 ? 0 : 1, reasons);
            return o;
        }

        /// <summary>
        /// ONE BAR OF AN AREA OR PATH SYSTEM (RebarInSystem), read into the Revit-free facts
        /// RebarSystemBars.Row turns into the row the takeoff, the schedule and reconcile aggregate.
        /// Read off the element like a set: Revit's own total length, volume and quantity.
        /// </summary>
        public static JObject DescribeInSystem(Document doc, RebarInSystem bar)
        {
            return RebarSystemBars.Row(ReadInSystem(doc, bar));
        }

        public static SystemBarFacts ReadInSystem(Document doc, RebarInSystem bar)
        {
            var f = new SystemBarFacts
            {
                Id = Rid.Value(bar.Id),
                UniqueId = Safe(() => bar.UniqueId),
                Name = Safe(() => bar.Name)
            };

            ElementId systemId = null;
            try { systemId = bar.SystemId; } catch { }
            Element system = (systemId != null && systemId != ElementId.InvalidElementId) ? doc.GetElement(systemId) : null;
            f.SystemId = systemId == null || systemId == ElementId.InvalidElementId ? (long?)null : Rid.Value(systemId);
            f.SystemKind = system is AreaReinforcement ? RebarSystemBars.KindArea
                         : system is PathReinforcement ? RebarSystemBars.KindPath : null;

            ElementId hostId = null;
            try { hostId = bar.GetHostId(); } catch { }
            Element host = (hostId != null && hostId != ElementId.InvalidElementId) ? doc.GetElement(hostId) : null;
            f.HostId = hostId == null || hostId == ElementId.InvalidElementId ? (long?)null : Rid.Value(hostId);
            f.HostResolved = host != null;
            if (host != null)
            {
                f.HostCategory = host.Category == null ? null : host.Category.Name;
                f.HostName = Safe(() => host.Name);
            }

            RebarBarType barType = null;
            try { barType = doc.GetElement(bar.GetTypeId()) as RebarBarType; } catch { }
            if (barType != null)
            {
                f.BarTypeId = Rid.Value(barType.Id);
                f.BarTypeName = Safe(() => barType.Name);
                f.NominalDiameterMm = Safe(() => barType.BarNominalDiameter) * FtToMm;
                f.ModelDiameterMm = Safe(() => barType.BarModelDiameter) * FtToMm;
            }

            ElementId shapeId = null;
            try { shapeId = bar.RebarShapeId; } catch { }
            RebarShape shape = (shapeId != null && shapeId != ElementId.InvalidElementId) ? doc.GetElement(shapeId) as RebarShape : null;
            if (shape != null)
            {
                f.ShapeId = Rid.Value(shape.Id);
                f.ShapeName = Safe(() => shape.Name);
                try { f.Style = shape.RebarStyle == RebarStyle.StirrupTie ? StructuralStyle.StirrupTie : StructuralStyle.Standard; }
                catch { }
            }

            f.LayoutRule = Safe(() => bar.LayoutRule.ToString());
            f.NumberOfBarPositions = Safe(() => (int?)bar.NumberOfBarPositions);
            f.Quantity = Safe(() => (int?)bar.Quantity);
            f.MaxSpacingMm = Safe(() => bar.MaxSpacing) * FtToMm;
            f.ArrayLengthMm = Safe(() => bar.ArrayLength) * FtToMm;

            for (int end = 0; end <= 1; end++)
            {
                ElementId hookId = null; bool readable = true;
                try { hookId = bar.GetHookTypeId(end); } catch { readable = false; }
                bool has = hookId != null && hookId != ElementId.InvalidElementId;
                Element hook = has ? doc.GetElement(hookId) : null;
                long? id = has ? Rid.Value(hookId) : (long?)null;
                string name = hook == null ? null : Safe(() => hook.Name);
                if (end == 0) { f.HookTypeId0 = id; f.HookTypeName0 = name; f.HookReadable0 = readable; }
                else { f.HookTypeId1 = id; f.HookTypeName1 = name; f.HookReadable1 = readable; }
            }

            f.TotalLengthMm = Safe(() => bar.TotalLength) * FtToMm;
            double? volumeFt3 = Safe(() => bar.Volume);
            f.VolumeM3 = volumeFt3.HasValue ? (double?)Guard.ToM3(volumeFt3.Value) : null;
            f.ScheduleMark = Safe(() => bar.ScheduleMark);

            IList<Curve> drawn = null;
            try { drawn = bar.GetCenterlineCurves(false, false, false); } catch { }
            if (drawn != null && drawn.Count > 0)
            {
                double sum = 0;
                foreach (Curve c in drawn) { try { sum += c.Length; } catch { } }
                f.OneBarCentrelineMm = sum * FtToMm;
                f.CentrelinePointsMm = Tessellate(drawn);
            }
            IList<Curve> declared = null;
            try { declared = bar.GetCenterlineCurves(false, true, true); } catch { }
            if (declared != null && declared.Count > 0) f.CentrelinePointsAsDeclaredMm = Tessellate(declared);
            return f;
        }

        /// <summary>
        /// ONE FABRIC SHEET (stage 1c), read into the Revit-free facts RebarSystemBars.FabricRow turns into the
        /// takeoff's row: every wire centreline Revit returns for each direction of the sheet AS PLACED, each
        /// direction's wire type and diameter, and Revit's own masses (cut sheet, the type's sheet and per area).
        /// </summary>
        public static JObject DescribeFabric(Document doc, FabricSheet sheet)
        {
            return RebarSystemBars.FabricRow(ReadFabric(doc, sheet));
        }

        public static FabricSheetFacts ReadFabric(Document doc, FabricSheet sheet)
        {
            var f = new FabricSheetFacts
            {
                Id = Rid.Value(sheet.Id),
                UniqueId = Safe(() => sheet.UniqueId),
                Name = Safe(() => sheet.Name)
            };
            ElementId area = null;
            try { area = sheet.FabricAreaOwnerId; } catch { }
            f.FabricAreaId = area == null || area == ElementId.InvalidElementId ? (long?)null : Rid.Value(area);

            ElementId hostId = null;
            try { hostId = sheet.HostId; } catch { }
            Element host = (hostId != null && hostId != ElementId.InvalidElementId) ? doc.GetElement(hostId) : null;
            f.HostId = hostId == null || hostId == ElementId.InvalidElementId ? (long?)null : Rid.Value(hostId);
            f.HostResolved = host != null;
            if (host != null)
            {
                f.HostCategory = host.Category == null ? null : host.Category.Name;
                f.HostName = Safe(() => host.Name);
            }

            FabricSheetType type = null;
            try { type = doc.GetElement(sheet.GetTypeId()) as FabricSheetType; } catch { }
            if (type != null)
            {
                f.SheetTypeId = Rid.Value(type.Id);
                f.SheetTypeName = Safe(() => type.Name);
                FabricWireType major = null, minor = null;
                try { major = doc.GetElement(type.MajorDirectionWireType) as FabricWireType; } catch { }
                try { minor = doc.GetElement(type.MinorDirectionWireType) as FabricWireType; } catch { }
                if (major != null) { f.MajorWireTypeName = Safe(() => major.Name); f.MajorWireDiameterMm = Safe(() => major.WireDiameter) * FtToMm; }
                if (minor != null) { f.MinorWireTypeName = Safe(() => minor.Name); f.MinorWireDiameterMm = Safe(() => minor.WireDiameter) * FtToMm; }
                f.RevitTypeSheetMassKg = Kg(Safe(() => type.SheetMass));
                double? perAreaInternal = Safe(() => type.SheetMassUnit);
                if (perAreaInternal.HasValue)
                    try { f.RevitTypeMassPerAreaKgM2 = UnitUtils.ConvertFromInternalUnits(perAreaInternal.Value, UnitTypeId.KilogramsPerSquareMeter); }
                    catch { f.RevitTypeMassPerAreaKgM2 = null; }
            }

            string number = null;
            try { number = sheet.get_Parameter(BuiltInParameter.FABRIC_NUMBER)?.AsString(); } catch { }
            f.FabricNumber = number;
            f.MajorWireLengthsMm = WireLengths(sheet, WireDistributionDirection.Major);
            f.MinorWireLengthsMm = WireLengths(sheet, WireDistributionDirection.Minor);
            f.CutOverallLengthMm = Safe(() => sheet.CutOverallLength) * FtToMm;
            f.CutOverallWidthMm = Safe(() => sheet.CutOverallWidth) * FtToMm;
            f.IsBent = Safe(() => (bool?)sheet.IsBent);
            f.RevitCutSheetMassKg = Kg(Safe(() => sheet.CutSheetMass));
            return f;
        }

        private static List<double> WireLengths(FabricSheet sheet, WireDistributionDirection direction)
        {
            IList<Curve> wires;
            try { wires = sheet.GetWireCenterlines(direction); } catch { return null; }
            if (wires == null) return null;
            var lengths = new List<double>(wires.Count);
            foreach (Curve c in wires)
            {
                try { lengths.Add(c.Length * FtToMm); } catch { return null; }
            }
            return lengths;
        }

        private static double? Kg(double? internalMass)
        {
            if (!internalMass.HasValue) return null;
            try { return UnitUtils.ConvertFromInternalUnits(internalMass.Value, UnitTypeId.Kilograms); }
            catch { return null; }
        }

        private static List<double[]> Tessellate(IList<Curve> curves)
        {
            var pts = new List<double[]>();
            double[] previous = null;
            foreach (Curve c in curves)
            {
                IList<XYZ> tess;
                try { tess = c.Tessellate(); } catch { return null; }
                if (tess == null) return null;
                foreach (XYZ q in tess)
                {
                    var v = new[] { q.X * FtToMm, q.Y * FtToMm, q.Z * FtToMm };
                    if (previous != null && Math.Abs(v[0] - previous[0]) < 1e-6 &&
                        Math.Abs(v[1] - previous[1]) < 1e-6 && Math.Abs(v[2] - previous[2]) < 1e-6) continue;
                    pts.Add(v);
                    previous = v;
                }
            }
            return pts.Count >= 2 ? pts : null;
        }

        /// <summary>
        /// The centreline as Revit draws it, for ONE bar of the set. The count and
        /// the summed length are the substance: a bar whose curves cannot be read
        /// is a bar nobody has measured, whatever its parameters say.
        /// </summary>
        /// <summary>
        /// The centreline of ONE bar of a set, as points, in millimetres.
        ///
        /// THE ONLY PLACE this is computed. There were three: this file, the audit
        /// and the apply - and the apply's took curve END POINTS, so every corner
        /// fillet became its chord and a 180 degree hook collapsed to a straight
        /// line with its outermost point discarded. The apply then measured
        /// containment on a bar that was not the one Revit drew, while its own
        /// header promised the plan, the apply and the audit run the same code.
        ///
        /// `asDeclared` asks Revit for the centreline WITHOUT hooks and WITHOUT
        /// bend radii - which is the shape a declaration draws. Comparing a
        /// declared polyline against the drawn one otherwise compares a hook-free
        /// statement with a hook-inclusive fact, and reports a difference of the
        /// hook length on every correctly built stirrup.
        /// </summary>
        /// <summary>One bar's centreline length as drawn (hooks and bends included), unrounded, or null.</summary>
        internal static double? OneBarCentrelineFt(Rebar bar)
        {
            try
            {
                IList<Curve> cs = bar.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, 0);
                if (cs == null || cs.Count == 0) return null;
                double sum = 0;
                foreach (Curve c in cs) sum += c.Length;
                return sum;
            }
            catch { return null; }
        }

        /// <summary>
        /// The side the hook at this end is DRAWN on (HookSide), or null: no hook, or a drawing that does not say.
        /// </summary>
        private static string DrawnSide(Rebar bar, XYZ normal, int end, ElementId hookId,
                                        ref List<double[]> drawn, ref List<double[]> declared)
        {
            if (normal == null || hookId == null || hookId == ElementId.InvalidElementId) return null;
            try
            {
                if (drawn == null) drawn = CentrelinePointsMm(bar, false);
                if (declared == null) declared = CentrelinePointsMm(bar, true);
                string why;
                return HookSide.Measure(drawn, declared, new[] { normal.X, normal.Y, normal.Z }, end, out why);
            }
            catch { return null; }
        }

        public static List<double[]> CentrelinePointsMm(Rebar bar, bool asDeclared)
        {
            IList<Curve> curves;
            try
            {
                curves = bar.GetCenterlineCurves(false, asDeclared, asDeclared,
                                                 MultiplanarOption.IncludeAllMultiplanarCurves, 0);
            }
            catch { return null; }
            if (curves == null || curves.Count == 0) return null;

            var pts = new List<double[]>();
            double[] previous = null;
            foreach (Curve c in curves)
            {
                IList<XYZ> tess;
                try { tess = c.Tessellate(); } catch { return null; }
                if (tess == null) return null;
                foreach (XYZ q in tess)
                {
                    var v = new[] { q.X * FtToMm, q.Y * FtToMm, q.Z * FtToMm };
                    if (previous != null && Math.Abs(v[0] - previous[0]) < 1e-6 &&
                        Math.Abs(v[1] - previous[1]) < 1e-6 && Math.Abs(v[2] - previous[2]) < 1e-6) continue;
                    pts.Add(v);
                    previous = v;
                }
            }
            return pts.Count >= 2 ? pts : null;
        }

        private static JArray PointArray(List<double[]> pts)
        {
            var a = new JArray();
            if (pts == null) return a;
            foreach (double[] p in pts)
                a.Add(new JArray(Math.Round(p[0], 3), Math.Round(p[1], 3), Math.Round(p[2], 3)));
            return a;
        }

        private static JObject Geometry(Rebar bar, JArray reasons)
        {
            var g = new JObject();
            IList<Curve> curves = null;
            try
            {
                curves = bar.GetCenterlineCurves(false, false, false,
                                                 MultiplanarOption.IncludeAllMultiplanarCurves, 0);
            }
            catch { }
            if (curves == null)
            {
                g["centreline"] = "unreadable";
                reasons.Add(StructuralCoverage.Reason("centreline",
                    "the bar would not return its centreline curves.", Rid.Value(bar.Id)));
                return g;
            }

            double total = 0;
            var ends = new JArray();
            foreach (Curve c in curves)
            {
                try { total += c.Length; } catch { }
                try
                {
                    ends.Add(new JObject
                    {
                        ["start_mm"] = Xyz(c.GetEndPoint(0), 3, FtToMm),
                        ["end_mm"] = Xyz(c.GetEndPoint(1), 3, FtToMm),
                        ["length_mm"] = Round(c.Length * FtToMm),
                        ["is_line"] = c is Line
                    });
                }
                catch { }
            }
            // TWO POLYLINES, because two different questions are asked of them.
            List<double[]> drawn = CentrelinePointsMm(bar, false);
            List<double[]> declaredForm = CentrelinePointsMm(bar, true);

            g["centreline"] = "read";
            if (drawn != null)
            {
                g["centreline_points_mm"] = PointArray(drawn);
                g["centreline_points_mean"] =
                    "the centreline AS DRAWN - hooks, bends and all - tessellated, so an arc is measured as an " +
                    "arc rather than as the chord between its ends. This is the steel, and it is what " +
                    "containment is measured on.";
            }
            else
            {
                reasons.Add(StructuralCoverage.Reason("centreline_points",
                    "the centreline curves would not tessellate, so the shape was not compared point by point.",
                    Rid.Value(bar.Id)));
            }
            if (declaredForm != null)
            {
                g["centreline_points_as_declared_mm"] = PointArray(declaredForm);
                g["centreline_points_as_declared_mean"] =
                    "the same bar with its HOOKS and its BEND RADII suppressed - the shape a declaration draws. " +
                    "The point-by-point comparison uses this one: comparing a hook-free declaration against a " +
                    "hook-inclusive fact reports the length of the hook as a difference on every correctly " +
                    "built stirrup.";
            }
            g["centreline_curve_count"] = curves.Count;
            g["centreline_length_mm"] = Round(total * FtToMm);
            g["centreline_length_means"] =
                "the length of ONE bar in the set as Revit draws it, hooks included. Multiply by quantity for " +
                "the steel, or read measured.total_length_mm, which Revit computes itself.";
            g["segments"] = ends;
            return g;
        }

        // ------------------------------------------------------------- helpers

        /// <summary>Revit's enum name, in the vocabulary a requirement set uses.</summary>
        public static string LayoutWord(string revitRule)
        {
            switch (revitRule)
            {
                case "Single": return RebarLayout.Single;
                case "FixedNumber": return RebarLayout.FixedNumber;
                case "NumberWithSpacing": return RebarLayout.NumberWithSpacing;
                case "MaximumSpacing": return RebarLayout.MaximumSpacing;
                case "MinimumClearSpacing": return RebarLayout.MinimumClearSpacing;
                // No default word. An unmapped rule is reported as itself rather
                // than folded into the nearest one we do know.
                default: return null;
            }
        }

        public static JObject Xyz(XYZ p, int digits, double scale = 1.0)
        {
            if (p == null) return null;
            return new JObject
            {
                ["x"] = Math.Round(p.X * scale, digits),
                ["y"] = Math.Round(p.Y * scale, digits),
                ["z"] = Math.Round(p.Z * scale, digits)
            };
        }

        private static JToken Round(double? v, int digits = 3)
        {
            if (!v.HasValue) return JValue.CreateNull();
            return new JValue(Math.Round(v.Value, digits));
        }

        private static string Safe(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }

        private static double? Safe(Func<double> f)
        {
            try { return f(); } catch { return null; }
        }

        private static int? Safe(Func<int?> f)
        {
            try { return f(); } catch { return null; }
        }

        private static bool? Safe(Func<bool?> f)
        {
            try { return f(); } catch { return null; }
        }

        private static bool? Safe(Func<bool> f)
        {
            try { return f(); } catch { return null; }
        }
    }
}
