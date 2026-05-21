using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetViewScaleHandler : IRevitCommand
    {
        public string Name => "set_view_scale";
        public string Description => "Set the graphical scale of a view. scale is the denominator (e.g., 50 for 1:50).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer""},""scale"":{""type"":""integer""}},""required"":[""scale""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var viewIdParam = req.Value<long?>("view_id");
            var scale = req.Value<int?>("scale");
            if (!scale.HasValue || scale.Value <= 0) return CommandResult.Fail("scale must be a positive integer.");

            var view = viewIdParam.HasValue
                ? doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View
                : uidoc.ActiveView;
            if (view == null) return CommandResult.Fail("Could not resolve view.");
            if (view.IsTemplate) return CommandResult.Fail("Cannot modify a view template.");

            using (var tx = new Transaction(doc, "RvtMcp: Set view scale"))
            {
                tx.Start();
                try
                {
                    var parameter = view.get_Parameter(BuiltInParameter.VIEW_SCALE);
                    if (parameter == null || parameter.IsReadOnly)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("View does not accept scale changes (VIEW_SCALE parameter not writable).");
                    }

                    var previous = view.Scale;
                    parameter.Set(scale.Value);
                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        view_id = RevitCompat.GetId(view.Id),
                        previous_scale = previous,
                        new_scale = scale.Value
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to set view scale: {ex.Message}");
                }
            }
        }
    }
}
