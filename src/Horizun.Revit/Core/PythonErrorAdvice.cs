using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
namespace Horizun.Revit.Core
{
    public static class PythonErrorAdvice
    {
        public static void Attach(JObject detail, string error, int year = 0)
        {
            JObject summary = detail?["script_error"] as JObject;
            if (detail == null || summary == null) return;
            JObject advice = Find(error, year, (int?)summary["line"]);
            if (advice == null && ErrorCodes.ShortTypeName((string)summary["type"]) == "AttributeError" &&
                !string.IsNullOrWhiteSpace((string)summary["hint"]))
                advice = new JObject { ["cause"] = (string)summary["message"], ["example"] = (string)summary["hint"] };
            if (advice != null) detail["advice"] = advice;
        }
        public static JObject Find(string error, int year = 0, int? line = null)
        {
            if (string.IsNullOrEmpty(error)) return null;
            string cause = null, example = null;
            if (Has(error, "'ElementId' object has no attribute 'IntegerValue'") || Has(error, "'ElementId' object has no attribute 'Value'")) {
                // Field (Cénit, 2026-11-09): ElementId.IntegerValue is gone in Revit 2026 (deprecated since 2024); .Value does not
                // exist before 2024. horizun.id_value reads whichever this Revit has.
                bool modern = Has(error, "'IntegerValue'");
                cause = modern
                    ? "ElementId.IntegerValue was removed in Revit 2026 (deprecated since 2024): use .Value, or horizun.id_value(element_id), which works on 2023-2027."
                    : "ElementId.Value does not exist before Revit 2024: use horizun.id_value(element_id), which works on 2023-2027.";
                example = "n = horizun.id_value(element.Id)  # int on every Revit 2023-2027\nsame = horizun.id_value(a) == horizun.id_value(b)";
            } else if (Has(error, "'InternalDefinition' object has no attribute 'ParameterType'")) {
                cause = "ParameterType is absent in newer APIs; use horizun.param_data_type (GetDataType in Revit 2022+).";
                example = "data_type = horizun.param_data_type(definition)  # or definition.GetDataType() in 2022+";
            } else if (Has(error, "'FamilyInstance' object has no attribute 'WallType'")) {
                cause = "The collector contains a FamilyInstance, not a Wall; filter its class.";
                example = "walls = horizun.of_class(elements, Wall)  # or collector.OfClass(Wall)";
            } else if (Has(error, "iteration over non-sequence of type NoneType") || Has(error, "'NoneType' object is not iterable")) {
                cause = "The geometry or collection returned None; check it before iterating.";
                example = "geometry = element.get_Geometry(Options())\nfor item in horizun.iter_safe(geometry): pass";
            } else if (Has(error, "A sub-transaction can only be active inside")) {
                cause = "A SubTransaction requires an open Transaction in the same document.";
                example = "with horizun.transaction('edit'):\n    pass";
            } else if (Has(error, "New project document failed")) {
                cause = "The template may be missing or invalid; check it or use the typed new_project operation.";
                example = "horizun_document_session({operation: new_project, save_as_path: <new .rvt path>})";
            } else if (Has(error, "referenced object is not valid")) {
                cause = "The object may have been invalidated by deletion or reload; resolve its UniqueId again.";
                example = "element = doc.GetElement(unique_id)\nif element is not None: pass";
            } else if (Has(error, "memoria protegida") || Has(error, "protected memory") || Has(error, "AccessViolation")) {
                // Field 2026-11-09: SystemError at line 294 of a long script. Revit's native side faulted; the
                // process may be unstable, and re-running the same call can repeat the fault or crash Revit.
                cause = "Revit's native code faulted (access violation) at " + (line.HasValue ? "line " + line : "the script line in the traceback") +
                        ": usually an object used after its document, view or transaction ended, or a native call on an element of another document. " +
                        "Revit may now be unstable: save the work with horizun_save_document before anything else and do not repeat the same call.";
                example = "# keep each API object inside the document/transaction that produced it\nwith horizun.transaction('edit'):\n    element = doc.GetElement(unique_id)\n    if element is not None: pass";
            } else if (Has(error, "One of the conditions for the inputs was not satisfied")) {
                // Field 2026-11-09: Revit ArgumentsInconsistentException raised by a Revit API call.
                cause = "A Revit API call at " + (line.HasValue ? "line " + line : "the script line in the traceback") +
                        " got arguments that are individually valid but inconsistent together (e.g. a curve not in the sketch plane, a point outside the host, a type of another category).";
                example = "# check the call's documented conditions first:\n# horizun_api_help(operation=\"describe\", type_name=\"<the type>\", member_filter=\"<the method>\")";
            } else if (Has(error, "Parameter name: reference")) {
                // Field 2026-11-09: "Parameter name: reference2" from a dimension or alignment call.
                cause = "A Reference argument is invalid for this call: the reference must come from geometry computed with Options(ComputeReferences=True) " +
                        "(or a plane/grid/level reference) in the same view and document; a reference read from another view or element is rejected.";
                example = "opts = Options()\nopts.ComputeReferences = True\nopts.View = view\nrefs = ReferenceArray()  # build both references from this geometry";
            } else if (Has(error, "ArgumentException")) {
                cause = "Check for null arguments, invalid IDs and lengths in internal feet at " + (line.HasValue ? "line " + line : "the script line in the traceback") + ".";
                example = "element = doc.GetElement(element_id)\nif element is not None: pass  # lengths are in feet";
            } else {
                var match = Regex.Match(error, @"'(?<class>[^']+)' object has no attribute '(?<member>[^']+)'", RegexOptions.CultureInvariant);
                if (match.Success) {
                    string cls = match.Groups["class"].Value, member = match.Groups["member"].Value;
                    cause = cls + "." + member + " is unavailable on this object in Revit " + (year > 0 ? year.ToString() : "(year unavailable)") + "; inspect the actual class and API.";
                    example = "horizun_api_help(operation='describe', type_name='" + cls + "', member_filter='" + member + "')  # also dir(obj)";
                } else if (Has(error, "name 'Autodesk' is not defined")) { cause = "Autodesk is not bound in the script scope."; example = "import Autodesk"; }
                else if (Has(error, "Invalid \\escape")) { cause = "A JSON path has an unescaped backslash."; example = "horizun.safe_json_path(path)"; }
                else if (Has(error, "Cannot import name IFCExportConfigurationsMap") || (Has(error, "ImportError") && Has(error, "IFCExportConfiguration"))) { cause = "IFC exporter UI classes may be unavailable."; example = "horizun_deliver_ifc (Pro edition)(...)"; }
            }
            return cause == null ? null : new JObject { ["cause"] = cause, ["example"] = example };
        }
        private static bool Has(string text, string part) => text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
