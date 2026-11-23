// Parameter writes shared by the typed writers (create_elements, stairs, write_params): resolve a parameter by
// BuiltInParameter, GUID or name, validate and apply a JSON value by storage type, and read it back.
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class TypeParameterWrites
    {
        internal static Parameter ResolveParameter(Element element, string spec, out string why)
        {
            why = null;
            if (Enum.TryParse(spec, true, out BuiltInParameter bip) && Enum.IsDefined(typeof(BuiltInParameter), bip))
            {
                Parameter parameter = element.get_Parameter(bip);
                if (parameter == null) why = "BuiltInParameter exists but is not present on this type";
                return parameter;
            }
            if (Guid.TryParse(spec, out Guid guid))
            {
                Parameter parameter = element.get_Parameter(guid);
                if (parameter == null) why = "shared-parameter GUID is not present on this type";
                return parameter;
            }
            IList<Parameter> hits = element.GetParameters(spec);
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1) { why = hits.Count + " parameters share that name; use BuiltInParameter or GUID"; return null; }
            // Reported 2026-10-16 (INFORME_AMPLIACION_EST §4.6): the visible name "Wire Diameter"
            // did not resolve on a fabric wire type; only FABRIC_WIRE_DIAMETER did. A unique
            // case- and spacing-insensitive match is accepted; otherwise the refusal names the
            // parameters the type DOES carry that look alike, each with its BuiltInParameter.
            var all = new List<Parameter>();
            try { foreach (Parameter p in element.Parameters) if (p?.Definition != null) all.Add(p); } catch { }
            string wanted = Fold(spec);
            var loose = all.Where(p => Fold(SafeDefinitionName(p)) == wanted).ToList();
            if (loose.Count == 1) return loose[0];
            // A built-in parameter's visible name follows Revit's language ("Comments" is "Comentarios" on a
            // Spanish Revit), and plans and callers write the English one. With no parameter of that name,
            // exact or loose, the English name of a common built-in resolves to its BuiltInParameter. After
            // the loose match, so a user parameter spelled alike keeps resolving (or refusing) as before.
            if (loose.Count == 0 && EnglishBuiltInNames.TryGetValue(spec.Trim(), out BuiltInParameter english))
            {
                Parameter parameter = element.get_Parameter(english);
                if (parameter != null) return parameter;
            }
            var words = spec.Split(new[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToArray();
            var alike = all.Where(p => words.Any(w => w.Length > 2 && SafeDefinitionName(p).ToLowerInvariant().Contains(w)))
                           .Select(p => SafeDefinitionName(p) + BipSuffix(p)).Distinct().Take(8).ToList();
            why = (loose.Count > 1 ? loose.Count + " parameters match that name loosely; use BuiltInParameter or GUID"
                                   : "no exact parameter name matched") +
                  (alike.Count > 0 ? ". Similar on this type: " + string.Join("; ", alike) : "");
            return null;
        }

        private static readonly Dictionary<string, BuiltInParameter> EnglishBuiltInNames =
            new Dictionary<string, BuiltInParameter>(StringComparer.OrdinalIgnoreCase)
            {
                ["Comments"] = BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
                ["Mark"] = BuiltInParameter.ALL_MODEL_MARK,
                ["Type Comments"] = BuiltInParameter.ALL_MODEL_TYPE_COMMENTS,
                ["Description"] = BuiltInParameter.ALL_MODEL_DESCRIPTION,
                ["Keynote"] = BuiltInParameter.KEYNOTE_PARAM,
                ["Model"] = BuiltInParameter.ALL_MODEL_MODEL,
                ["Manufacturer"] = BuiltInParameter.ALL_MODEL_MANUFACTURER,
                ["URL"] = BuiltInParameter.ALL_MODEL_URL,
                ["Cost"] = BuiltInParameter.ALL_MODEL_COST,
            };

        private static string Fold(string s) =>
            new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        private static string SafeDefinitionName(Parameter p) { try { return p.Definition?.Name ?? ""; } catch { return ""; } }

        private static string BipSuffix(Parameter p)
        {
            try
            {
                if (p.Definition is InternalDefinition d && d.BuiltInParameter != BuiltInParameter.INVALID)
                    return " (" + d.BuiltInParameter + ")";
            }
            catch { }
            return "";
        }

        private static double Finite(double value, string field)
        { if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException(field + " must be finite"); return value; }

        /// <summary>
        /// A bare number written to a length parameter is raw feet. "Unconnected Height": 2700 meant as
        /// millimetres wrote 823 m and the re-read confirmed it, because the re-read compares against the same
        /// number. When the number is not the size of a building part (over 30 m, or under 0.1 mm) the write is
        /// refused with the string or the feet to send instead. Null when the write may go ahead.
        /// </summary>
        internal static string RawLengthRefusal(Parameter parameter, string spec, JToken value)
        {
            if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)) return null;
            if (parameter == null || parameter.StorageType != StorageType.Double) return null;
            bool isLength;
            try { isLength = parameter.Definition.GetDataType() == SpecTypeId.Length; } catch { isLength = false; }
            if (!isLength) return null;
            string warning = LengthWriteRules.MagnitudeWarning(spec, value.Value<double>());
            return warning == null ? null : "refused, " + warning + " A real length of that size goes as a string with " +
                "its unit (\"45 m\", \"147.6 ft\"), which Revit parses in that unit. Nothing was written.";
        }

        internal static void ValidateValue(Parameter parameter, JToken value)
        {
            if (value == null || value.Type == JTokenType.Null)
            {
                if (parameter.StorageType != StorageType.ElementId) throw new ArgumentException("null is only accepted to clear ElementId storage");
                return;
            }
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    if (value.Type == JTokenType.String)
                    {
                        string edge = TextValueRules.EdgeWhitespaceError(parameter.Definition?.Name ?? "value", value.Value<string>());
                        if (edge != null) throw new ArgumentException(edge);
                    }
                    return;
                case StorageType.Integer:
                    if (value.Type != JTokenType.Integer && value.Type != JTokenType.Boolean && value.Type != JTokenType.String)
                        throw new ArgumentException("Integer storage requires integer, boolean or unit-aware string"); return;
                case StorageType.Double:
                    if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float && value.Type != JTokenType.String)
                        throw new ArgumentException("Double storage requires number or unit-aware string");
                    if (value.Type != JTokenType.String) Finite(value.Value<double>(), "Double parameter value");
                    return;
                case StorageType.ElementId:
                    if (value.Type != JTokenType.Integer && value.Type != JTokenType.String)
                        throw new ArgumentException("ElementId storage requires an integer id, numeric string or null"); return;
                default: throw new ArgumentException("unsupported storage type " + parameter.StorageType);
            }
        }
        internal static void Apply(Parameter parameter, Write write)
        {
            JToken value = write.Requested;
            bool accepted;
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    string text = value.Type == JTokenType.String ? value.Value<string>() : value.ToString(Formatting.None);
                    write.Expected = text; accepted = parameter.Set(text); break;
                case StorageType.Integer:
                    int integer;
                    if (value.Type == JTokenType.String)
                    {
                        if (!int.TryParse(value.Value<string>(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out integer))
                            throw new ArgumentException("Integer parameter needs an invariant integer, not an unverified display string.");
                    }
                    else integer = value.Type == JTokenType.Boolean ? (value.Value<bool>() ? 1 : 0) : value.Value<int>();
                    write.Expected = integer; accepted = parameter.Set(integer); break;
                case StorageType.Double:
                    double number;
                    if (value.Type == JTokenType.String)
                    {
                        if (!UnitFormatUtils.TryParse(parameter.Element.Document.GetUnits(), parameter.Definition.GetDataType(), value.Value<string>(), out number))
                            throw new ArgumentException("Cannot parse parameter '" + write.Spec + "' with the document's units.");
                        write.ParsedByRevit = true;
                    }
                    else number = value.Value<double>();
                    Finite(number, "Double parameter value");
                    write.Expected = number; accepted = parameter.Set(number); break;
                case StorageType.ElementId:
                    long raw = value == null || value.Type == JTokenType.Null ? -1 : value.Value<long>();
                    if (!Rid.CanRepresent(raw)) throw new InvalidOperationException("ElementId value is outside range");
                    write.Expected = raw; accepted = parameter.Set(Rid.Make(raw)); break;
                default: throw new InvalidOperationException("unsupported storage type " + parameter.StorageType);
            }
            if (!accepted) throw new InvalidOperationException("Revit rejected Set for parameter '" + write.Spec + "'");
            // Expected was derived from the requested value BEFORE Set, never from its result.
        }

        internal static JToken Read(Parameter parameter)
        {
            if (parameter == null) return JValue.CreateNull();
            switch (parameter.StorageType)
            {
                case StorageType.String: return new JValue(parameter.AsString());
                case StorageType.Integer: return new JValue(parameter.AsInteger());
                case StorageType.Double: return new JValue(parameter.AsDouble());
                case StorageType.ElementId: return new JValue(Rid.Value(parameter.AsElementId()));
                default: return JValue.CreateNull();
            }
        }

        internal sealed class Write { public string Spec; public JToken Requested, Expected; public bool ParsedByRevit; }
    }
}
