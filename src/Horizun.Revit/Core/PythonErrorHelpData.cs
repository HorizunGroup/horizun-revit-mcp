using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    // Small, ordered data table. Patterns are deliberately narrow: an unknown name
    // is not necessarily a Revit API type, and an unknown exception needs no guess.
    public static class PythonErrorHelpData
    {
        private static readonly string[,] Rules = {
            { "InvalidOperationException", "modification.*outside.*transaction", "For a read, remove the modification; for a write, open a Transaction on the document." },
            { "TypeError", "(?:ICollection|List).*ElementId|ElementId.*(?:ICollection|List)", "Use from System.Collections.Generic import List and build List[ElementId](), adding each ElementId." },
            { "AttributeError", "'(?<class>[^']+)' object has no attribute '(?<member>[^']+)'", "Inspect the actual type and member with horizun_api_help before retrying." },
            { "NameError", "name '(?<name>ElementId|FilteredElementCollector|Transaction|Wall|XYZ|Options|BuiltInCategory)' is not defined", "Import the missing Revit DB name: from Autodesk.Revit.DB import {name}." }
        };
        public static JObject Build(JObject summary, string source)
        {
            if (summary == null) return null;
            string type = (string)summary["type"], message = (string)summary["message"] ?? "";
            int? line = (int?)summary["line"];
            string[] lines = (source ?? "").Replace("\r\n", "\n").Split('\n');
            var result = new JObject { ["line"] = line, ["code"] = line > 0 && line <= lines.Length ? (JToken)lines[line.Value - 1] : JValue.CreateNull(),
                ["type"] = type, ["message"] = message, ["hint"] = null };
            for (int i = 0; i < Rules.GetLength(0); i++)
            {
                if (ErrorCodes.ShortTypeName(type) != Rules[i, 0]) continue;
                Match m = Regex.Match(message, Rules[i, 1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!m.Success) continue;
                string hint = Rules[i, 2].Replace("{name}", m.Groups["name"].Value);
                if (m.Groups["class"].Success) hint += " Type: " + m.Groups["class"].Value + "; member: " + m.Groups["member"].Value + ".";
                result["hint"] = hint;
                break;
            }
            return result;
        }
    }
}
