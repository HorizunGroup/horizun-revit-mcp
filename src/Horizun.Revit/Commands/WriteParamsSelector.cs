// -----------------------------------------------------------------------------
// Horizun - original Horizun code.
//
// horizun_write_params_verified 'selector' + 'set': the writes are GENERATED for
// every element the selector matches (Core/SelectorWriteRules). This file only
// resolves the selector (SelectorResolver, shared with delete_verified mode=query
// and measure_geometry) and, for only_if_empty, reads whether each cell is empty.
// The generated rows then ride the ordinary path unchanged: resolved, rehearsed,
// bound by the token (the request hash binds selector/set/only_if_empty and the
// resolved plan binds every generated row, so a model that matches different
// elements at apply refuses as stale), one transaction, re-read row by row.
// -----------------------------------------------------------------------------
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public partial class WriteParamsCommand
    {
        private static CommandResult ExpandSelector(Document doc, JObject request, out JArray writes, out JObject report)
        {
            writes = new JArray(); report = null;
            if (!ElementSelectorRules.TryParse(request["selector"], out SelectorSpec spec, out string selectorError))
                return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, "selector: " + selectorError + " Nothing was written.");
            string setError = SelectorWriteRules.SetError(request["set"]);
            if (setError != null) return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, setError + " Nothing was written.");
            JToken onlyToken = request["only_if_empty"];
            if (onlyToken != null && onlyToken.Type != JTokenType.Boolean && onlyToken.Type != JTokenType.Null)
                return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, "only_if_empty must be true or false. Nothing was written.");
            bool onlyIfEmpty = onlyToken != null && onlyToken.Type == JTokenType.Boolean && (bool)onlyToken;
            var set = (JObject)request["set"];

            SelectorResolution found = SelectorResolver.Resolve(doc, spec, SelectorWriteRules.MaxTargets);
            string sizeError = SelectorWriteRules.SizeError(found.Matched.Count, found.Truncated, set.Count);
            if (sizeError != null) return CommandResult.FailCoded(ErrorCodes.PrecondicionNoCumplida, sizeError + " Nothing was written.");
            if (found.Unknown.Count > 0)
                return CommandResult.FailCoded(ErrorCodes.PrecondicionNoCumplida,
                    found.Unknown.Count + " element(s) could not be read against the selector (first ids: " +
                    string.Join(", ", found.Unknown.Take(10)) + "), so what it matches is not known. Nothing was written.");

            writes = SelectorWriteRules.Expand(found.Matched, set, onlyIfEmpty, (id, name) => CellIsEmpty(doc, id, name), out int skipped);
            report = new JObject
            {
                ["matched"] = found.Matched.Count,
                ["examined"] = found.Examined,
                ["undatable"] = found.Undatable,
                ["parameters"] = new JArray(set.Properties().Select(p => p.Name)),
                ["only_if_empty"] = onlyIfEmpty,
                ["skipped_not_empty"] = skipped,
                ["writes_generated"] = writes.Count,
                ["first_ids"] = new JArray(found.Matched.Take(20))
            };
            return null;
        }

        /// <summary>True when the parameter holds no value; null when it cannot be resolved or read (the row then names why).</summary>
        private static bool? CellIsEmpty(Document doc, long id, string spec)
        {
            try
            {
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                if (e == null) return null;
                Parameter p = ResolveParameter(e, spec, out string matchedBy, out string why);
                if (p == null) return null;
                if (!p.HasValue) return true;
                if (p.StorageType == StorageType.String) return string.IsNullOrEmpty(p.AsString());
                if (p.StorageType == StorageType.ElementId) return p.AsElementId() == ElementId.InvalidElementId;
                return false;
            }
            catch { return null; }
        }
    }
}
