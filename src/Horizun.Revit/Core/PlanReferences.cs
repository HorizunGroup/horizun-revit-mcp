// -----------------------------------------------------------------------------
// Revit-free result references used by horizun_execute_plan.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
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

        /// <summary>
        /// Bind every reference in an action to the exact canonical arguments its rehearsal
        /// produced. Object-property order is ignored; array order and scalar types are not.
        /// </summary>
        public static JObject DescribeBinding(JToken original, JToken resolved)
        {
            var references = new JArray();
            CollectReferences(original, resolved, "", references);
            return new JObject
            {
                ["original_hash"] = CanonicalFingerprint(original),
                ["resolved_hash"] = CanonicalFingerprint(resolved),
                ["references"] = references
            };
        }

        /// <summary>Compare apply-time resolved arguments with the exact approved binding.</summary>
        public static JObject CompareBinding(JObject expected, JToken original, JToken actual)
        {
            string expectedHash = expected?.Value<string>("resolved_hash");
            string actualHash = CanonicalFingerprint(actual);
            var now = DescribeBinding(original, actual);
            return new JObject
            {
                ["code"] = "reference_binding_changed",
                ["matches"] = !string.IsNullOrEmpty(expectedHash) &&
                              string.Equals(expectedHash, actualHash, StringComparison.Ordinal),
                ["expected_resolved_hash"] = expectedHash,
                ["actual_resolved_hash"] = actualHash,
                ["expected_references"] = expected?["references"]?.DeepClone() ?? new JArray(),
                ["actual_references"] = now["references"]
            };
        }

        public static string CanonicalFingerprint(JToken token)
            => ConfirmationStore.HashPlan("json=" + Canonical(token));

        private static string Canonical(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return "null";
            var o = token as JObject;
            if (o != null)
            {
                var sb = new StringBuilder("{");
                bool first = true;
                foreach (JProperty p in o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonConvert.ToString(p.Name)).Append(':').Append(Canonical(p.Value));
                }
                return sb.Append('}').ToString();
            }
            var a = token as JArray;
            if (a != null)
            {
                var sb = new StringBuilder("[");
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Canonical(a[i]));
                }
                return sb.Append(']').ToString();
            }
            return token.ToString(Formatting.None);
        }

        private static void CollectReferences(JToken original, JToken resolved, string pointer, JArray rows)
        {
            if (original == null) return;
            string key, path;
            if (original.Type == JTokenType.String && TryParse((string)original, out key, out path))
            {
                rows.Add(new JObject
                {
                    ["pointer"] = pointer.Length == 0 ? "/" : pointer,
                    ["expression"] = (string)original,
                    ["source_action"] = key,
                    ["source_path"] = path,
                    ["resolved_value"] = resolved?.DeepClone() ?? JValue.CreateNull()
                });
                return;
            }
            var oo = original as JObject;
            var ro = resolved as JObject;
            if (oo != null)
            {
                foreach (JProperty p in oo.Properties())
                    CollectReferences(p.Value, ro?[p.Name], pointer + "/" + EscapePointer(p.Name), rows);
                return;
            }
            var oa = original as JArray;
            var ra = resolved as JArray;
            if (oa != null)
                for (int i = 0; i < oa.Count; i++)
                    CollectReferences(oa[i], ra != null && i < ra.Count ? ra[i] : null,
                                      pointer + "/" + i, rows);
        }

        private static string EscapePointer(string value)
            => (value ?? "").Replace("~", "~0").Replace("/", "~1");

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
