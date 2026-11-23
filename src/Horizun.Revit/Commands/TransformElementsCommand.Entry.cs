using System.Linq;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class TransformElementsCommand
    {
        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return Failure(CommandResult.Fail("Parameters must be a JSON object: " + ex.Message)); }
            var notes = ReliabilityDefaults.Curves(request).ToArray();
            CommandResult result = ExecuteTransform(app, request);
            return result.Success ? AssumptionNotes.StampResult(result, notes)
                : Failure(AssumptionNotes.MarkFailure(result, "SUPUESTO used; ", notes));
        }

        private static CommandResult Failure(CommandResult result) => CommandResult.FailWithDetail(
            result.Error, TransformFailure.ClassifyUnspecified(result.Error, result.Detail), result.Fallback, result.CapabilityGaps);

        // JoinPeers has sorted each end's members. Preserve the two end positions.
        internal static void BindEndJoinPeers(PlannedElement wall, string[][] peers)
        {
            wall.BeforeValues["joined_neighbors_by_end"] = JToken.FromObject(peers).ToString(Formatting.None);
        }
    }
}
