// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_api_help (read-only): the members of a Revit API type as THIS Revit loads it,
// read by reflection from the RevitAPI / RevitAPIUI assemblies in this process. Touches
// no document. Rules (lookup, suggestions, member rows) in Core/ApiHelpRules.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ApiHelpCommand : ICommand
    {
        public string Name => "horizun_api_help";
        public string Description => "Read-only: describe loaded Revit API members or check declared CLR argument types against their signatures.";

        private static List<Type> cache;
        private static readonly object CacheGate = new object();

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, "Parameters must be a JSON object: " + ex.Message); }
            string signatureError = ApiHelpRules.ValidateSignatureRequest(request);
            if (signatureError != null) return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, signatureError);
            string operation = (string)request["operation"] ?? "describe";
            string name = ((string)request["type_name"] ?? "").Trim();
            if (name.Length < 2) return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, "type_name is required, e.g. WallSweep or Autodesk.Revit.DB.WallSweep.");
            int max = request.Value<int?>("max_members") ?? ApiHelpRules.DefaultMaxMembers;
            if (max < 1 || max > 500) return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, "max_members must be between 1 and 500.");
            bool inherited = request.Value<bool?>("include_inherited") ?? (operation == "describe");
            string filter = ((string)request["member_filter"])?.Trim();

            List<Type> types;
            lock (CacheGate)
            {
                if (cache == null)
                    cache = ApiHelpRules.PublicTypes(AppDomain.CurrentDomain.GetAssemblies()
                        .Where(a => { string n = a.GetName().Name; return n == "RevitAPI" || n == "RevitAPIUI" || n == "RevitAPIIFC"; }));
                types = cache;
            }
            string version = null;
            try { version = app.Application.VersionNumber + " (" + app.Application.VersionBuild + ")"; } catch { }

            List<Type> found = ApiHelpRules.Find(types, name);
            if (found.Count == 0)
                return CommandResult.FailCoded(ErrorCodes.ElementoNoEncontrado,
                    "No public Revit API type named '" + name + "' in this Revit " + version + ". Closest: " +
                    string.Join(", ", ApiHelpRules.Suggest(types, name)) + ".");
            if (found.Count > 1)
                return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido,
                    "'" + name + "' names " + found.Count + " types; send the full name: " + string.Join(", ", found.Select(t => t.FullName)) + ".");

            if (operation == "check_signature")
            {
                // An explicit BCL allowlist plus the already cached public Revit types.
                // No Assembly.Load/Type.GetType with caller input, nor a method invocation.
                var allowed = types.Concat(ApiHelpRules.PublicTypes(new[]
                {
                    typeof(object).Assembly, typeof(List<>).Assembly, typeof(IEnumerable<>).Assembly,
                    typeof(System.Collections.IEnumerable).Assembly, typeof(Uri).Assembly
                }.Distinct()));
                JObject checkedSignature = ApiSignatureRules.Check(found[0], filter,
                    ((JArray)request["argument_types"]).Select(t => (string)t).ToList(), allowed, inherited,
                    app.Application.VersionNumber);
                checkedSignature["revit_version"] = version;
                return CommandResult.Ok(checkedSignature);
            }
            JObject described = ApiHelpRules.Describe(found[0], filter, inherited, max);
            described["revit_version"] = version;
            described["member_filter"] = filter;
            described["note"] = "Read from the assemblies this Revit loaded, by reflection: what a script can call here, in this version. " +
                                "Python sees properties as attributes and out parameters as extra return values.";
            JArray traps = ApiHelpRules.FieldTraps(found[0]);
            if (traps.Count > 0) described["field_traps"] = traps;
            JArray fieldNotes = ApiFieldNotes.For(found[0].Name);
            if (fieldNotes != null) described["field_notes"] = fieldNotes;   // measured traps (field report 2026-11-17)
            return CommandResult.Ok(described);
        }
    }
}
