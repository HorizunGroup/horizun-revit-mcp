// -----------------------------------------------------------------------------
// Revit-free result references used by horizun_execute_plan.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class PlanReferences
    {
        public static bool HasReference(JToken token)
        {
            if (token == null) return false;
            if (token.Type == JTokenType.String && TryParse((string)token, out _, out _)) return true;
            if (token is JContainer c)
                foreach (JToken child in c.Children()) if (HasReference(child)) return true;
            return false;
        }

        public static IEnumerable<string> ReferenceKeys(JToken token)
        {
            if (token == null) yield break;
            string key; string path;
            if (token.Type == JTokenType.String && TryParse((string)token, out key, out path))
            {
                yield return key;
                yield break;
            }
            if (token is JContainer c)
                foreach (JToken child in c.Children())
                    foreach (string found in ReferenceKeys(child)) yield return found;
        }

        public static JToken Resolve(JToken token, IDictionary<string, JToken> results, out string error)
        {
            error = null;
            if (token == null) return null;
            string key; string path;
            if (token.Type == JTokenType.String && TryParse((string)token, out key, out path))
            {
                JToken root;
                if (results == null || !results.TryGetValue(key, out root))
                { error = "No completed action named '" + key + "' is available for reference '" + token + "'."; return null; }
                JToken value = Walk(root, path, out error);
                return value?.DeepClone();
            }
            if (token is JObject o)
            {
                var copy = new JObject();
                foreach (JProperty property in o.Properties())
                {
                    JToken value = Resolve(property.Value, results, out error);
                    if (error != null) return null;
                    copy[property.Name] = value;
                }
                return copy;
            }
            if (token is JArray a)
            {
                var copy = new JArray();
                foreach (JToken item in a)
                {
                    JToken value = Resolve(item, results, out error);
                    if (error != null) return null;
                    copy.Add(value);
                }
                return copy;
            }
            return token.DeepClone();
        }

        private static bool TryParse(string value, out string key, out string path)
        {
            key = null; path = null;
            if (string.IsNullOrEmpty(value) || value.Length < 4 ||
                !value.StartsWith("${", StringComparison.Ordinal) || !value.EndsWith("}", StringComparison.Ordinal))
                return false;
            string body = value.Substring(2, value.Length - 3);
            int dot = body.IndexOf('.');
            key = dot < 0 ? body : body.Substring(0, dot);
            path = dot < 0 ? "" : body.Substring(dot + 1);
            return key.Length > 0;
        }

        private static JToken Walk(JToken value, string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path)) return value;
            JToken current = value;
            foreach (string segment in path.Split('.'))
            {
                if (current is JObject o) current = o[segment];
                else if (current is JArray a && int.TryParse(segment, out int index) && index >= 0 && index < a.Count)
                    current = a[index];
                else current = null;
                if (current == null)
                { error = "Result path '" + path + "' does not exist."; return null; }
            }
            return current;
        }
    }
}
