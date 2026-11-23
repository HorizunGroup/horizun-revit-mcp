// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A PYTHON FAILURE THE AGENT CAN FIX IN ONE TRY: the line of ITS script, the code
// around it, the exception type and message, and - for the Revit API errors that
// recur in the field - what to do instead.
//
// The full IronPython traceback still travels; this is the short block that goes
// first. Field signals (October 2026): the commonest failures were the script's own
// logic and misuse of the API, and the reply's first lines were the bridge's frames
// and a message cut at 400 characters, so the next attempt often repeated the
// mistake.
//
// Pure: it reads the formatted traceback text and the source that ran.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ScriptErrorSummary
    {
        private static readonly Regex Frame = new Regex(
            "^\\s*File \"(?<file>[^\"]*)\", line (?<line>\\d+)(?:, in (?<fn>\\S+))?", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex ExceptionLine = new Regex(
            @"^(?<type>[A-Za-z_][\w.]*(?:Error|Exception|Exit|Interrupt|Iteration|Warning)?)(?::\s?(?<msg>.*))?$", RegexOptions.CultureInvariant);

        private static readonly string[] BridgeFunctions =
            { "_hz_", "__hz_", "checkpoint", "revit_raised", "dialog_answer", "__enter__", "__exit__" };

        /// <summary>(fragment of the message, hint). First match wins.</summary>
        private static readonly KeyValuePair<string, string>[] Hints =
        {
            H("referenced object is not valid",
              "The object may be invalid after being deleted, reloaded or rolled back. Keep its UniqueId and " +
              "resolve it again with doc.GetElement(unique_id), checking for None."),
            H("Close is not allowed when there is any open",
              "Commit or RollBack every Transaction / SubTransaction / TransactionGroup on that document (in a finally) before Close()."),
            H("Modification of the document is forbidden",
              "Writes need an open transaction: wrap them in `with horizun.transaction('what it does'):` (commits, or rolls back on error)."),
            H("outside of transaction",
              "Writes need an open transaction: wrap them in `with horizun.transaction('what it does'):` (commits, or rolls back on error)."),
            H("has no attribute 'IntegerValue'",
              "ElementId.Value is the 64-bit member in Revit 2024+; IntegerValue is the legacy member. Use " +
              "hz_compat.eid_int(element_id) for Revit 2023-2027, or read the loaded members with " +
              "horizun_api_help(operation=\"describe\", type_name=\"Autodesk.Revit.DB.ElementId\")."),
            H("has no attribute 'Value'",
              "ElementId.IntegerValue is the legacy member. Use hz_compat.eid_int(element_id) across Revit 2023-2027."),
            H("name 'unicode' is not defined",
              "This is Python 3: use str(value), or import hz_compat and use hz_compat.unicode."),
            H("name 'basestring' is not defined",
              "This is Python 3: use isinstance(value, str), or import hz_compat and use hz_compat.string_types."),
            H("name 'long' is not defined",
              "This is Python 3: int is the only integer type. Use int(value) or import hz_compat and use hz_compat.long."),
            H("ParameterType",
              "ParameterType is replaced by ForgeTypeId-based definition data types in newer Revit APIs. Use " +
              "hz_compat.parameter_data_type(definition); inspect Definition with horizun_api_help."),
            H("DisplayUnitType",
              "Newer Revit APIs use UnitTypeId/ForgeTypeId where older APIs used DisplayUnitType. Use " +
              "hz_compat.unit_type_id(modern_name, legacy_name) and inspect UnitUtils with horizun_api_help."),
            H("BuiltInParameterGroup",
              "Newer Revit APIs use GroupTypeId where older APIs used BuiltInParameterGroup. Use " +
              "hz_compat.parameter_group(name) and inspect the loaded API with horizun_api_help."),
            H("is not JSON serializable",
              "Convert .NET values before json: int(x) for ids and integers, float(x) for doubles, str(x) for enums and Guids."),
            H("'NoneType' object has no attribute",
              "Something returned None: doc.GetElement, LookupParameter, get_Parameter, FirstElement or a failed lookup. Check it " +
              "before using it, and report what was missing in __output__."),
            H("expected ElementId, got",
              "This API takes an ElementId, not a number: ElementId(n) (64-bit since 2024; use int on 2023)."),
            H("ICollection[ElementId]",
              "A Python list is not a CLR ICollection<ElementId>. Use `from System.Collections.Generic import List; ids = List[ElementId]()` " +
              "and ids.Add(ElementId(n)) for each id; inspect the exact overload with horizun_api_help."),
            H("name 'System' is not defined",
              "System is not pre-imported. Add `import System` before using System.Array or other System members."),
            H("Cannot import name",
              "That .NET type is in an assembly the engine has not loaded: add clr.AddReference('<assembly>') before the import."),
            H("No module named",
              "Not a module of IronPython's standard library or a loaded assembly: clr.AddReference('<assembly>') for .NET, or avoid it."),
            H("only for connectors of PhysicalConn",
              "Inspect ConnectorType before accessing this property. Filter to ConnectorType.End only if the task " +
              "requires end connectors; account for other connector types instead of silently discarding them. " +
              "Handle the documented exception per connector and report skipped items. Inspect the loaded API with " +
              "horizun_api_help(operation=\"describe\", type_name=\"Autodesk.Revit.DB.Connector\")."),
            H("The iterator cannot proceed due to changes",
              "The collector was modified while iterating: materialise it first, e.g. ids = list(collector.ToElementIds())."),
            H("StopIteration",
              "next() found nothing: the collector or generator was empty. Use next(iter(x), None) and check for None."),
        };

        private static KeyValuePair<string, string> H(string k, string v) => new KeyValuePair<string, string>(k, v);

        /// <summary>
        /// Null when no frame of the caller's script can be found. <paramref name="scriptFile"/> is
        /// the code_path, or null for inline code (IronPython names it &lt;string&gt;).
        /// </summary>
        public static JObject Build(string traceback, string code, string scriptFile)
        {
            if (string.IsNullOrEmpty(traceback)) return null;
            string[] lines = traceback.Replace("\r\n", "\n").Split('\n');

            string type = null, message = null;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string l = lines[i].TrimEnd();
                if (l.Length == 0 || l.StartsWith(" ", StringComparison.Ordinal) || l.StartsWith("---", StringComparison.Ordinal)) continue;
                if (l.StartsWith("Traceback", StringComparison.Ordinal)) break;
                Match em = ExceptionLine.Match(l);
                if (em.Success) { type = em.Groups["type"].Value; message = em.Groups["msg"].Success ? em.Groups["msg"].Value : ""; }
                else { message = l; }
                break;
            }

            string wanted = string.IsNullOrEmpty(scriptFile) ? "<string>" : scriptFile;
            int line = 0;
            string function = null;
            foreach (Match fm in Frame.Matches(traceback))
            {
                if (!SameFile(fm.Groups["file"].Value, wanted)) continue;
                string fn = fm.Groups["fn"].Success ? fm.Groups["fn"].Value : null;
                if (fn != null && BridgeFunctions.Any(b => fn.StartsWith(b, StringComparison.Ordinal))) continue;
                line = int.Parse(fm.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
                function = fn;
            }

            var result = new JObject
            {
                ["type"] = type,
                ["message"] = message,
                ["line"] = line > 0 ? (JToken)line : null,
                ["function"] = function,
                ["file"] = string.IsNullOrEmpty(scriptFile) ? "inline code" : scriptFile
            };

            string[] src = (code ?? "").Replace("\r\n", "\n").Split('\n');
            if (line > 0 && line <= src.Length)
            {
                var context = new JArray();
                for (int n = Math.Max(1, line - 2); n <= Math.Min(src.Length, line + 2); n++)
                    context.Add((n == line ? ">" : " ") + n.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(5) +
                                " | " + Clip(src[n - 1], 160));
                result["context"] = context;
            }

            string all = (type ?? "") + ": " + (message ?? "") + "\n" + traceback;
            foreach (var hint in Hints)
                if (all.IndexOf(hint.Key, StringComparison.Ordinal) >= 0) { result["hint"] = hint.Value; break; }

            if (result["hint"] == null)
            {
                string bare = ErrorCodes.ShortTypeName(type);
                if (bare == "AttributeError")
                    result["hint"] = "Read the actual type and member with horizun_api_help (type_name and member_filter). " +
                        "Revit API members and enum values are case-sensitive and may differ by Revit year; do not guess an alias.";
                else if (bare == "TypeError" || bare == "ArgumentException" || bare == "ArgumentOutOfRangeException" || bare == "ArgumentsInconsistentException")
                    result["hint"] = "Inspect the exact constructor or method overload with horizun_api_help operation=check_signature " +
                        "(type_name, exact member_filter or .ctor, and argument_types as CLR names). " +
                        "Match CLR argument types, validate non-null elements, and convert lengths to Revit internal units before retrying.";
                else if (bare == "NameError")
                    result["hint"] = "Import or define the missing name before use. The request's arguments arrive as HORIZUN_ARGS_JSON: " +
                        "use `import json; arguments = json.loads(HORIZUN_ARGS_JSON)` and check keys; inputs do not become script globals.";
            }

            string errorType = ErrorCodes.ShortTypeName(type);
            if (errorType == "TypeError" || errorType == "ArgumentException" || errorType == "ArgumentOutOfRangeException" ||
                errorType == "ArgumentsInconsistentException" || errorType == "AmbiguousMatchException" ||
                errorType == "MissingMethodException" || errorType == "InvalidCastException")
            {
                // Append even when a more specific hint won (e.g. ICollection<ElementId>).
                // A CLR stack frame identifies the declaring type/member. Multiple
                // different API members are ambiguous: never guess one from proximity.
                var targets = Regex.Matches(traceback,
                    @"^\s*(?:at|en)\s+(?<type>Autodesk\.Revit\.(?:DB|UI)(?:\.[A-Za-z_]\w*)+)\.(?<member>\.ctor|[A-Za-z_]\w*)\s*\((?<parameters>[^()\r\n]*)\)",
                    RegexOptions.CultureInvariant | RegexOptions.Multiline)
                    .Cast<Match>().Select(m => new { Type = m.Groups["type"].Value, Member = m.Groups["member"].Value,
                        SingleParameter = !string.IsNullOrWhiteSpace(m.Groups["parameters"].Value) && !m.Groups["parameters"].Value.Contains(",") })
                    .GroupBy(m => m.Type + "/" + m.Member).Select(g => new
                    {
                        g.First().Type, g.First().Member, SingleParameter = g.All(m => m.SingleParameter)
                    }).ToList();
                string guidance = " Use horizun_api_help operation=check_signature with the actual CLR argument_types; " +
                    "it compares signatures without invoking the API. Python int/list do not establish CLR Int32/Int64/ICollection types.";
                if (targets.Count == 1)
                {
                    var target = targets[0];
                    // Only explicitly named CLR 'got' types establish actual argument
                    // types. In particular Python 'int' is NOT inferred to be Int32.
                    Match actual = Regex.Match(message ?? "", @"^\s*expected\s+[\w.]+,\s*got\s+(?<type>System\.[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*$",
                        RegexOptions.CultureInvariant);
                    var arguments = new JObject
                    {
                        ["operation"] = "check_signature", ["type_name"] = target.Type,
                        ["member_filter"] = target.Member, ["include_inherited"] = false
                    };
                    if (target.SingleParameter && actual.Success)
                        arguments["argument_types"] = new JArray(actual.Groups["type"].Value);
                    result["signature_check"] = new JObject
                    {
                        ["tool"] = "horizun_api_help", ["arguments"] = arguments,
                        ["argument_types_required"] = arguments["argument_types"] == null,
                        ["basis"] = "explicit CLR type and member in traceback; no source or receiver type inference"
                    };
                    guidance = " Check this member: horizun_api_help(" +
                        "operation=\"check_signature\", type_name=\"" + target.Type + "\", member_filter=\"" + target.Member + "\", argument_types=" +
                        (arguments["argument_types"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "CLR_TYPES") + "). " +
                        (arguments["argument_types"] == null
                            ? "Replace CLR_TYPES with the actual CLR type names in argument order ([] only for zero arguments); the traceback does not establish them. "
                            : "The single CLR argument type is explicitly reported by the exception. ") +
                        "This reads signatures; it does not prove concrete values are valid.";
                }
                result["hint"] = ((string)result["hint"] ?? "Inspect CLR overload compatibility.") + guidance;
            }

            if (type == null && line == 0) return null;
            return result;
        }

        /// <summary>The short block that goes first in the error text.</summary>
        public static string Render(JObject summary)
        {
            if (summary == null) return null;
            var sb = new StringBuilder();
            sb.Append("Python error");
            if (summary["line"] != null && summary["line"].Type == JTokenType.Integer) sb.Append(" at line ").Append((int)summary["line"]);
            sb.Append(" of the script: ").Append((string)summary["type"] ?? "error");
            string msg = (string)summary["message"];
            if (!string.IsNullOrEmpty(msg)) sb.Append(": ").Append(Clip(msg, 300));
            sb.Append('\n');
            if (summary["context"] is JArray ctx)
                foreach (JToken l in ctx) sb.Append((string)l).Append('\n');
            if (summary["hint"] != null) sb.Append("Fix: ").Append((string)summary["hint"]).Append('\n');
            return sb.ToString();
        }

        /// <summary>Offer matching API types for an unresolved Python name, never asserting intent.</summary>
        public static void AttachNameApiHelp(JObject summary, IList<Type> apiTypes, string revitVersion)
        {
            if ((string)summary?["type"] != "NameError" || apiTypes == null) return;
            var match = Regex.Match((string)summary["message"] ?? "", @"^name '(?<name>[A-Za-z_][A-Za-z_0-9]{0,127})' is not defined$", RegexOptions.CultureInvariant);
            if (!match.Success) return;
            string name = match.Groups["name"].Value;
            var exact = ApiHelpRules.Find(apiTypes, name);
            var names = exact.Select(t => t.FullName).Where(n => n != null).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            bool approximate = names.Count == 0;
            if (approximate) names = ApiHelpRules.Suggest(apiTypes, name, int.MaxValue);
            if (names.Count == 0) return;
            summary["api_name_candidates"] = new JObject { ["revit_version"] = revitVersion,
                ["type_names"] = new JArray(names.Take(ApiHelpRules.MaxSuggestions)), ["total"] = names.Count,
                ["basis"] = (approximate ? "approximate API type name matches" : "API type name match only") +
                    "; the missing name may instead be a script variable" };
            string first = names[0];
            summary["hint"] = ((string)summary["hint"] ?? "") + " If this name means a Revit API type, candidates in the loaded API: " +
                string.Join(", ", names.Take(ApiHelpRules.MaxSuggestions)) + ". Verify intent and namespace, then inspect e.g. " +
                "horizun_api_help(operation=\"describe\", type_name=\"" + first + "\").";
        }

        /// <summary>Reflect overloads only when the traceback identifies one CLR member.</summary>
        public static void AttachSignatureApiHelp(JObject summary, IList<Type> apiTypes, string revitVersion)
        {
            var request = summary?["signature_check"]?["arguments"] as JObject;
            if (request == null || apiTypes == null) return;
            string typeName = (string)request["type_name"], member = (string)request["member_filter"];
            var matches = apiTypes.Where(t => t.FullName == typeName).ToList();
            if (matches.Count != 1 || string.IsNullOrEmpty(member)) return;
            var type = matches[0];
            string filter = member == ".ctor" ? type.Name : member;
            var description = ApiHelpRules.Describe(type, filter, false, int.MaxValue);
            var rows = ((JArray)description["members"]).OfType<JObject>()
                .Where(r => member == ".ctor" ? (string)r["kind"] == "constructor" :
                    (string)r["name"] == member && (string)r["kind"] == "method")
                .Select(r => r.DeepClone()).ToList();
            if (rows.Count == 0) return;
            summary["signature_check"]["revit_version"] = revitVersion;
            summary["signature_check"]["overloads_total"] = rows.Count;
            summary["signature_check"]["reflected_overloads"] = new JArray(rows.Take(ApiHelpRules.MaxSuggestions));
            summary["hint"] = ((string)summary["hint"] ?? "") + " Loaded API overloads (validate argument values separately): " +
                string.Join("; ", rows.Take(ApiHelpRules.MaxSuggestions).Select(r => (string)r["signature"])) +
                (rows.Count > ApiHelpRules.MaxSuggestions ? "; more overloads available via horizun_api_help." : ".");
        }

        /// <summary>Inspect the expected API type when the exception does not identify the failing member.</summary>
        public static void AttachExpectedTypeApiHelp(JObject summary, IList<Type> apiTypes, string revitVersion)
        {
            if (ErrorCodes.ShortTypeName((string)summary?["type"]) != "TypeError" || apiTypes == null ||
                summary["signature_check"] != null) return;
            var match = Regex.Match((string)summary["message"] ?? "",
                @"^expected (?<type>[A-Za-z_][A-Za-z0-9_]*), got [A-Za-z_][A-Za-z0-9_]*$",
                RegexOptions.CultureInvariant);
            if (!match.Success) return;
            var matches = apiTypes.Where(t => t.Name == match.Groups["type"].Value).ToList();
            if (matches.Count != 1) return;
            var type = matches[0];
            var description = ApiHelpRules.Describe(type, type.Name, false, int.MaxValue);
            var constructors = ((JArray)description["members"]).OfType<JObject>()
                .Where(row => (string)row["kind"] == "constructor").ToList();
            if (constructors.Count == 0) return;
            summary["expected_api_type"] = new JObject {
                ["type_name"] = type.FullName, ["revit_version"] = revitVersion,
                ["basis"] = "candidate expected type from error text; failing member and runtime type identity are not established",
                ["constructors_total"] = constructors.Count,
                ["constructors"] = new JArray(constructors.Take(ApiHelpRules.MaxSuggestions).Select(row => row.DeepClone())),
                ["tool"] = "horizun_api_help",
                ["arguments"] = new JObject { ["operation"] = "describe", ["type_name"] = type.FullName, ["member_filter"] = type.Name }
            };
            summary["hint"] = ((string)summary["hint"] ?? "") + " Candidate expected API type " + type.FullName +
                " has constructors: " + string.Join("; ", constructors.Take(ApiHelpRules.MaxSuggestions).Select(row => (string)row["signature"])) +
                ". The failing member and type identity are not established; verify them. Try horizun_api_help(operation=\"describe\", type_name=\"" +
                type.FullName + "\", member_filter=\"" + type.Name + "\").";
        }

        /// <summary>Offer, but do not assert, matches for the receiver named by IronPython.
        /// A simple type name in an error does not prove the object's CLR identity.</summary>
        public static void AttachAttributeApiHelp(JObject summary, IList<Type> apiTypes, string revitVersion)
        {
            if ((string)summary?["type"] != "AttributeError" || apiTypes == null) return;
            Match m = Regex.Match((string)summary["message"] ?? "",
                @"^'(?<type>[A-Za-z_][A-Za-z0-9_]*)' object has no attribute '(?<member>[A-Za-z_][A-Za-z0-9_]*)'",
                RegexOptions.CultureInvariant);
            if (!m.Success) return;
            string name = m.Groups["type"].Value, member = m.Groups["member"].Value;
            var found = apiTypes.Where(t => t.Name == name).ToList();
            if (found.Count != 1) return;
            Type type = found[0];
            JObject description = ApiHelpRules.Describe(type, member, true, ApiHelpRules.MaxSuggestions);
            var candidates = (JArray)description["closest_members"] ?? (JArray)description["members"];
            if (candidates == null || candidates.Count == 0) return;
            summary["api_suggestion"] = new JObject
            {
                ["type_name"] = type.FullName, ["revit_version"] = revitVersion,
                ["basis"] = "candidate: exception type name matches one loaded API type; receiver identity is not established",
                ["closest_members"] = candidates.DeepClone(),
                ["tool"] = "horizun_api_help",
                ["arguments"] = new JObject { ["operation"] = "describe", ["type_name"] = type.FullName, ["member_filter"] = member }
            };
            string names = string.Join("; ", candidates.Select(c => (string)c["signature"] ??
                ((string)c["type"] + " " + (string)c["name"])));
            summary["hint"] = ((string)summary["hint"] ?? "") + " Possible members on " + type.FullName +
                " in the loaded API (verify the receiver type): " + names + ". Try horizun_api_help(operation=\"describe\", type_name=\"" +
                type.FullName + "\", member_filter=\"" + member + "\").";
        }

        /// <summary>Suggest enum values when IronPython reports only the receiver as 'type'.
        /// Source text is a clue, not proof of the runtime receiver or its bindings.</summary>
        public static void AttachEnumApiHelp(JObject summary, string code, IList<Type> apiTypes, string revitVersion)
        {
            if ((string)summary?["type"] != "AttributeError" || apiTypes == null) return;
            var error = Regex.Match((string)summary["message"] ?? "",
                @"^'type' object has no attribute '(?<member>[A-Za-z_][A-Za-z0-9_]*)'$", RegexOptions.CultureInvariant);
            if (!error.Success || summary["line"]?.Type != JTokenType.Integer) return;
            string[] lines = (code ?? "").Replace("\r\n", "\n").Split('\n');
            int line = (int)summary["line"];
            if (line < 1 || line > lines.Length) return;
            string member = error.Groups["member"].Value;
            var names = Regex.Matches(lines[line - 1],
                @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*" + Regex.Escape(member) + @"\b", RegexOptions.CultureInvariant)
                .Cast<Match>().Select(m => m.Groups["name"].Value).Distinct().ToList();
            if (names.Count != 1) return;
            var types = apiTypes.Where(t => t.IsEnum && t.Name == names[0]).ToList();
            if (types.Count != 1) return;
            var type = types[0];
            var description = ApiHelpRules.Describe(type, member, true, ApiHelpRules.MaxSuggestions);
            var candidates = (JArray)description["closest_members"] ?? (JArray)description["members"];
            if (candidates == null || candidates.Count == 0) return;
            summary["api_suggestion"] = new JObject {
                ["type_name"] = type.FullName, ["revit_version"] = revitVersion,
                ["basis"] = "candidate from source text and loaded API enum; runtime receiver identity is not established",
                ["closest_members"] = candidates.DeepClone(), ["tool"] = "horizun_api_help",
                ["arguments"] = new JObject { ["operation"] = "describe", ["type_name"] = type.FullName, ["member_filter"] = member }
            };
            summary["hint"] = ((string)summary["hint"] ?? "") + " Possible values on " + type.FullName +
                " in the loaded API: " + string.Join(", ", candidates.Select(c => (string)c["name"])) +
                ". Source text does not establish the runtime receiver; verify it. Try horizun_api_help(operation=\"describe\", type_name=\"" +
                type.FullName + "\", member_filter=\"" + member + "\").";
        }

        private static bool SameFile(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
            (b != "<string>" && a.Replace('/', '\\').EndsWith(b.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));

        private static string Clip(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
