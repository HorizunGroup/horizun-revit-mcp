using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    internal static class DocumentContext
    {
        internal static IEnumerable<JObject> Levels(Document doc) => new FilteredElementCollector(doc)
            .OfClass(typeof(Level)).Cast<Level>().Select(l => new JObject
            {
                ["id"] = Rid.Value(l.Id), ["name"] = l.Name, ["elevation_m"] = l.Elevation * 0.3048
            });

        internal static IEnumerable<JObject> Views(Document doc) => new FilteredElementCollector(doc)
            .OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).Select(v => new JObject
            {
                ["id"] = Rid.Value(v.Id), ["name"] = v.Name, ["type"] = v.ViewType.ToString(),
                ["level_id"] = v.GenLevel == null ? JValue.CreateNull() : (JToken)Rid.Value(v.GenLevel.Id),
                ["level_name"] = v.GenLevel?.Name
            });

        internal static CommandResult MissingView(Document doc, string requested)
        {
            var detail = ContextLookup.Missing("view", requested, Views(doc));
            return CommandResult.FailCoded(ErrorCodes.ElementoNoEncontrado, ContextLookup.Message(detail), extra: detail);
        }

        // Batch validators already carry exception messages into each invalid row.
        internal static string Hint<T>(Document doc, string requested) where T : Element
        {
            if (typeof(T) == typeof(Level)) return " " + ContextLookup.Message(ContextLookup.Missing("level", requested, Levels(doc)));
            if (typeof(View).IsAssignableFrom(typeof(T)))
                return " " + ContextLookup.Message(ContextLookup.Missing("view", requested, Views(doc)));
            return "";
        }
    }
}
