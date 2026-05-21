using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Creates a plumbing pipe between two points. Coordinates in mm.
    /// Uses the canonical Pipe.Create(Document, systemTypeId, pipeTypeId, levelId, start, end)
    /// overload which is stable across Revit 2022-2027.
    /// </summary>
    public class CreatePipeHandler : IRevitCommand
    {
        private const double MmToFeet = 1.0 / 304.8;
        private const double FeetToMm = 304.8;

        public string Name => "create_pipe";

        public string Description =>
            "Create a plumbing pipe between two points. All coordinates and the diameter are " +
            "in millimeters. If pipe_type_id, system_type_id or level_id are omitted, the first " +
            "available PipeType / PipingSystemType and the Level nearest to start_z are used.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""start_x"",""start_y"",""start_z"",""end_x"",""end_y"",""end_z""],
  ""properties"": {
    ""start_x"": {""type"":""number""}, ""start_y"": {""type"":""number""}, ""start_z"": {""type"":""number""},
    ""end_x"": {""type"":""number""}, ""end_y"": {""type"":""number""}, ""end_z"": {""type"":""number""},
    ""pipe_type_id"": {""type"":""integer"",""description"":""PipeType ElementId. If omitted, first available.""},
    ""system_type_id"": {""type"":""integer"",""description"":""PipingSystemType ElementId. If omitted, first available.""},
    ""level_id"": {""type"":""integer"",""description"":""Level ElementId. If omitted, nearest to start_z.""},
    ""diameter"": {""type"":""number"",""description"":""Pipe diameter in mm.""}
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = JObject.Parse(paramsJson ?? "{}");
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Invalid JSON parameters: " + ex.Message);
            }

            // Required coordinates (mm).
            double startXmm, startYmm, startZmm, endXmm, endYmm, endZmm;
            try
            {
                startXmm = RequireDouble(request, "start_x");
                startYmm = RequireDouble(request, "start_y");
                startZmm = RequireDouble(request, "start_z");
                endXmm = RequireDouble(request, "end_x");
                endYmm = RequireDouble(request, "end_y");
                endZmm = RequireDouble(request, "end_z");
            }
            catch (ArgumentException ex)
            {
                return CommandResult.Fail(ex.Message);
            }

            var startPt = new XYZ(startXmm * MmToFeet, startYmm * MmToFeet, startZmm * MmToFeet);
            var endPt = new XYZ(endXmm * MmToFeet, endYmm * MmToFeet, endZmm * MmToFeet);

            if (startPt.DistanceTo(endPt) < 1e-7)
                return CommandResult.Fail("Start and end points are coincident; pipe has zero length.");

            // Resolve PipeType.
            PipeType pipeType;
            var pipeTypeId = request.Value<long?>("pipe_type_id");
            if (pipeTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(pipeTypeId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(pipeTypeId.Value));
                pipeType = doc.GetElement(RevitCompat.ToElementId(pipeTypeId.Value)) as PipeType;
                if (pipeType == null)
                    return CommandResult.Fail($"Element {pipeTypeId.Value} is not a valid PipeType.");
            }
            else
            {
                pipeType = FirstOfClass<PipeType>(doc);
                if (pipeType == null)
                    return CommandResult.Fail("No PipeType found in the project. Load a pipe family first.");
            }

            // Resolve PipingSystemType.
            PipingSystemType systemType;
            var systemTypeId = request.Value<long?>("system_type_id");
            if (systemTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(systemTypeId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(systemTypeId.Value));
                systemType = doc.GetElement(RevitCompat.ToElementId(systemTypeId.Value)) as PipingSystemType;
                if (systemType == null)
                    return CommandResult.Fail($"Element {systemTypeId.Value} is not a valid PipingSystemType.");
            }
            else
            {
                systemType = FirstOfClass<PipingSystemType>(doc);
                if (systemType == null)
                    return CommandResult.Fail("No PipingSystemType found in the project.");
            }

            // Resolve Level.
            Level level;
            var levelId = request.Value<long?>("level_id");
            if (levelId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(levelId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(levelId.Value));
                level = doc.GetElement(RevitCompat.ToElementId(levelId.Value)) as Level;
                if (level == null)
                    return CommandResult.Fail($"Element {levelId.Value} is not a valid Level.");
            }
            else
            {
                level = NearestLevel(doc, startZmm * MmToFeet);
                if (level == null)
                    return CommandResult.Fail("No Level found in the project.");
            }

            var diameterMm = request.Value<double?>("diameter");

            using (var tx = new Transaction(doc, "RvtMcp: create pipe"))
            {
                tx.Start();
                try
                {
                    var pipe = Pipe.Create(doc, systemType.Id, pipeType.Id, level.Id, startPt, endPt);
                    if (pipe == null)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("Pipe.Create returned null.");
                    }

                    if (diameterMm.HasValue && diameterMm.Value > 0)
                    {
                        var diamParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                        if (diamParam != null && !diamParam.IsReadOnly)
                            diamParam.Set(diameterMm.Value * MmToFeet);
                    }

                    // Read back actual values after creation.
                    double lengthMm = ReadLengthMm(pipe);
                    double actualDiameterMm = ReadDiameterMm(pipe);

                    tx.Commit();

                    return CommandResult.Ok(new
                    {
                        created = true,
                        pipe_id = RevitCompat.GetId(pipe.Id),
                        pipe_type = pipeType.Name,
                        system_type = systemType.Name,
                        level = level.Name,
                        length_mm = Math.Round(lengthMm, 1),
                        diameter_mm = Math.Round(actualDiameterMm, 1),
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to create pipe: " + ex.Message);
                }
            }
        }

        private static double RequireDouble(JObject request, string key)
        {
            var token = request[key];
            if (token == null || token.Type == JTokenType.Null)
                throw new ArgumentException($"Parameter '{key}' is required.");
            try
            {
                return token.Value<double>();
            }
            catch
            {
                throw new ArgumentException($"Parameter '{key}' must be a number.");
            }
        }

        private static T FirstOfClass<T>(Document doc) where T : Element
        {
            var collector = new FilteredElementCollector(doc).OfClass(typeof(T));
            foreach (T el in collector)
                return el;
            return null;
        }

        private static Level NearestLevel(Document doc, double elevationFt)
        {
            Level nearest = null;
            double bestDelta = double.MaxValue;
            var collector = new FilteredElementCollector(doc).OfClass(typeof(Level));
            foreach (Level lv in collector)
            {
                double delta = Math.Abs(lv.Elevation - elevationFt);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    nearest = lv;
                }
            }
            return nearest;
        }

        private static double ReadLengthMm(Pipe pipe)
        {
            try
            {
                var lengthParam = pipe.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                if (lengthParam != null && lengthParam.HasValue)
                    return lengthParam.AsDouble() * FeetToMm;
            }
            catch { }

            try
            {
                if (pipe.Location is LocationCurve lc && lc.Curve != null)
                    return lc.Curve.Length * FeetToMm;
            }
            catch { }

            return 0.0;
        }

        private static double ReadDiameterMm(Pipe pipe)
        {
            try
            {
                var diamParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (diamParam != null && diamParam.HasValue)
                    return diamParam.AsDouble() * FeetToMm;
            }
            catch { }

            try
            {
                return pipe.Diameter * FeetToMm;
            }
            catch { }

            return 0.0;
        }
    }
}
