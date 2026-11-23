// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// A JSON Schema validator for exactly the keyword subset the contract's input
// schemas use. It began as the examples test's private helper; it lives in the
// server now because two things must judge arguments with the SAME rules: the
// tests that prove the advertised tools/list copy gives the same verdicts as the
// full contract, and the advice a failed call carries (schema_help). Sharing one
// validator is what keeps that advice from contradicting the proof.
//
// It is ADVISORY. Nothing calls it before dispatch: what a call is validated
// against is ToolInputRules (Horizun.Contracts), the per-command parsers and the
// host tools' own refusals, all reading the full contract. Changing this file
// never changes what is accepted or rejected. (One rule IS enforced before
// dispatch, from the same contract: top-level arguments a closed schema does not
// declare - see UndeclaredArguments.cs.)
//
// Unknown keywords are ERRORS, never skipped. `format` is an annotation here, as it
// is by default in JSON Schema 2020-12; `default` and `description` are annotations.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class ContractSchemaCheck
    {
        // The patterns are the contract's, the strings are a caller's: bound each match
        // so a pathological input can never hold the advice path (it runs after a
        // failure, and a timeout there surfaces as "no advice", never as a hang).
        private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

        /// <summary>Validates <paramref name="value"/> against <paramref name="schema"/>
        /// and returns "pointer: message" strings; empty when it passes.</summary>
        /// <summary>The most errors one validation collects; the verdict needs one.</summary>
        internal const int MaxErrors = 1000;

        internal static List<string> Validate(JToken value, JToken schema)
        {
            var errors = new List<string>();
            Validate(value, schema, "", errors);
            return errors;
        }

        // A kit recipe is a call template: a <model value> is present but is not yet
        // a concrete value to type-check. Dispatch keeps using the strict overload.
        internal static List<string> ValidateRecipe(JToken value, JToken schema)
        {
            var errors = new List<string>();
            Validate(value, schema, "", errors, true);
            return errors;
        }

        private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.Ordinal)
        {
            "type", "enum", "const", "properties", "required", "additionalProperties", "items",
            "minItems", "maxItems", "uniqueItems", "minLength", "maxLength", "pattern",
            "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minProperties", "maxProperties",
            "allOf", "anyOf", "oneOf", "not", "if", "then", "else",
            "format", "default", "description", "title", "examples", "$comment", "propertyNames"
        };

        internal static IEnumerable<string> UnknownKeywords(JToken schema)
        {
            if (!(schema is JObject s)) yield break;
            foreach (JProperty p in s.Properties())
            {
                if (!Known.Contains(p.Name)) yield return p.Name;
                if (p.Name == "properties" && p.Value is JObject props)
                {
                    foreach (JProperty child in props.Properties())
                        foreach (string k in UnknownKeywords(child.Value)) yield return k;
                }
                else if (p.Name == "items" || p.Name == "additionalProperties" || p.Name == "propertyNames" || p.Name == "not" ||
                         p.Name == "if" || p.Name == "then" || p.Name == "else")
                {
                    foreach (string k in UnknownKeywords(p.Value)) yield return k;
                }
                else if ((p.Name == "allOf" || p.Name == "anyOf" || p.Name == "oneOf") && p.Value is JArray arr)
                {
                    foreach (JToken branch in arr)
                        foreach (string k in UnknownKeywords(branch)) yield return k;
                }
            }
        }

        internal static void Validate(JToken value, JToken schemaToken, string pointer, List<string> errors, bool recipe = false)
        {
            // Bounded: this now runs on production failures (SchemaHelp), where the input is
            // whatever a caller sent. A verdict only needs one error and advice keeps 20.
            if (errors.Count >= MaxErrors) return;
            if (schemaToken is JValue b && b.Type == JTokenType.Boolean)
            {
                if (!(bool)b) errors.Add(At(pointer) + "is not allowed here");
                return;
            }
            if (!(schemaToken is JObject schema)) { errors.Add(At(pointer) + "schema is not an object"); return; }
            if (recipe && value?.Type == JTokenType.String && Regex.IsMatch((string)value, @"<[^<>]+>", RegexOptions.None, PatternTimeout)) return;
            foreach (JProperty p in schema.Properties())
                if (!Known.Contains(p.Name)) errors.Add(At(pointer) + "the validator does not understand keyword '" + p.Name + "'");
            if (value is JObject named && schema["propertyNames"] != null)
                foreach (JProperty property in named.Properties()) Validate(new JValue(property.Name), schema["propertyNames"], pointer + "/" + property.Name, errors, recipe);

            if (schema["type"] != null)
            {
                IEnumerable<string> types = schema["type"] is JArray ta ? ta.Select(t => (string)t) : new[] { (string)schema["type"] };
                if (!types.Any(t => HasType(value, t)))
                {
                    errors.Add(At(pointer) + "must be " + schema["type"].ToString(Formatting.None) + ", got " + value.Type);
                    return;
                }
            }
            if (schema["const"] != null && !JToken.DeepEquals(schema["const"], value))
                errors.Add(At(pointer) + "must be " + schema["const"].ToString(Formatting.None));
            if (schema["enum"] is JArray allowed && !allowed.Any(a => JToken.DeepEquals(a, value)))
                errors.Add(At(pointer) + "must be one of " + allowed.ToString(Formatting.None) + ", got " + value.ToString(Formatting.None));

            switch (value.Type)
            {
                case JTokenType.String:
                    string s = (string)value;
                    if (schema["minLength"] != null && s.Length < (int)schema["minLength"]) errors.Add(At(pointer) + "is shorter than minLength");
                    if (schema["maxLength"] != null && s.Length > (int)schema["maxLength"]) errors.Add(At(pointer) + "is longer than maxLength");
                    if (schema["pattern"] != null && !Regex.IsMatch(s, (string)schema["pattern"], RegexOptions.None, PatternTimeout))
                        errors.Add(At(pointer) + "'" + s + "' does not match " + schema["pattern"]);
                    break;
                case JTokenType.Integer:
                case JTokenType.Float:
                    double d = value.Value<double>();
                    if (schema["minimum"] != null && d < schema["minimum"].Value<double>()) errors.Add(At(pointer) + "is below minimum");
                    if (schema["maximum"] != null && d > schema["maximum"].Value<double>()) errors.Add(At(pointer) + "is above maximum");
                    if (schema["exclusiveMinimum"] != null && d <= schema["exclusiveMinimum"].Value<double>()) errors.Add(At(pointer) + "is not above exclusiveMinimum");
                    if (schema["exclusiveMaximum"] != null && d >= schema["exclusiveMaximum"].Value<double>()) errors.Add(At(pointer) + "is not below exclusiveMaximum");
                    break;
                case JTokenType.Array:
                    var arr = (JArray)value;
                    if (schema["minItems"] != null && arr.Count < (int)schema["minItems"]) errors.Add(At(pointer) + "has fewer than minItems");
                    if (schema["maxItems"] != null && arr.Count > (int)schema["maxItems"]) errors.Add(At(pointer) + "has more than maxItems");
                    // uniqueItems: the first equal pair is the finding. Candidates are bucketed
                    // by a cheap key and compared exactly (DeepEquals) only inside a bucket, and
                    // an array already over maxItems is not scanned - a pairwise scan of a
                    // hostile array was quadratic in time and in strings (review 2026-09-26).
                    bool overMax = schema["maxItems"] != null && arr.Count > (int)schema["maxItems"];
                    if (!overMax && schema["uniqueItems"] != null && (bool)schema["uniqueItems"])
                    {
                        var buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                        for (int i = 0; i < arr.Count; i++)
                        {
                            string key = UniqueKey(arr[i]);
                            if (!buckets.TryGetValue(key, out List<int> same)) buckets[key] = same = new List<int>();
                            int j = -1;
                            foreach (int k in same) if (JToken.DeepEquals(arr[k], arr[i])) { j = k; break; }
                            if (j >= 0) { errors.Add(At(pointer) + "items " + j + " and " + i + " are equal"); break; }
                            same.Add(i);
                        }
                    }
                    if (schema["items"] != null)
                        for (int i = 0; i < arr.Count; i++) Validate(arr[i], schema["items"], pointer + "/" + i, errors, recipe);
                    break;
                case JTokenType.Object:
                    var obj = (JObject)value;
                    if (schema["minProperties"] != null && obj.Count < (int)schema["minProperties"]) errors.Add(At(pointer) + "has fewer than minProperties");
                    if (schema["maxProperties"] != null && obj.Count > (int)schema["maxProperties"]) errors.Add(At(pointer) + "has more than maxProperties");
                    if (schema["required"] is JArray required)
                        foreach (JToken r in required)
                            if (obj[(string)r] == null) errors.Add(At(pointer) + "'" + (string)r + "' is required");
                    var props = schema["properties"] as JObject;
                    foreach (JProperty p in obj.Properties())
                    {
                        string child = pointer + "/" + p.Name;
                        if (props?[p.Name] != null) Validate(p.Value, props[p.Name], child, errors, recipe);
                        else if (schema["additionalProperties"] != null) Validate(p.Value, schema["additionalProperties"], child, errors, recipe);
                    }
                    break;
            }

            if (schema["allOf"] is JArray all)
                foreach (JToken branch in all) Validate(value, branch, pointer, errors, recipe);
            if (schema["anyOf"] is JArray any && !any.Any(branch => Passes(value, branch, pointer, recipe)))
                errors.Add(At(pointer) + "matches none of anyOf: " + Diagnose(value, any, pointer, recipe));
            if (schema["oneOf"] is JArray one)
            {
                int matched = one.Count(branch => Passes(value, branch, pointer, recipe));
                if (matched != 1)
                    errors.Add(At(pointer) + "matches " + matched + " branches of oneOf (exactly one required)" +
                               (matched == 0 ? ": " + Diagnose(value, one, pointer, recipe) : ""));
            }
            if (schema["not"] != null && Passes(value, schema["not"], pointer, recipe))
                errors.Add(At(pointer) + "matches a schema it must not match");
            if (schema["if"] != null)
            {
                if (Passes(value, schema["if"], pointer, recipe)) { if (schema["then"] != null) Validate(value, schema["then"], pointer, errors, recipe); }
                else if (schema["else"] != null) Validate(value, schema["else"], pointer, errors, recipe);
            }
        }

        private static bool Passes(JToken value, JToken schema, string pointer, bool recipe = false)
        {
            var errors = new List<string>();
            Validate(value, schema, pointer, errors, recipe);
            return errors.Count == 0;
        }

        /// <summary>The errors of the closest branch, so a failing oneOf says WHY.
        ///
        /// "Closest" first means the branch the instance NAMES: a branch whose const
        /// properties all equal the instance's values (a create_elements row's kind). Only
        /// when no branch is named does the fewest-errors branch speak. Fewest errors alone
        /// told a wall row missing four fields to become a level, because the level branch
        /// lacked only two (MEASURED 2026-09-26) - advice pointing the wrong way.</summary>
        private static string Diagnose(JToken value, JArray branches, string pointer, bool recipe = false)
        {
            List<JToken> named = branches.Where(b => NamedBy(value, b)).ToList();
            List<string> best = null;
            foreach (JToken branch in named.Count > 0 ? (IEnumerable<JToken>)named : branches)
            {
                var errors = new List<string>();
                Validate(value, branch, pointer, errors, recipe);
                if (best == null || errors.Count < best.Count) best = errors;
            }
            return best == null ? "" : string.Join("; ", best.Take(5));
        }

        /// <summary>True when the branch has at least one const-valued property and the
        /// instance carries every one of them with exactly that value.</summary>
        private static bool NamedBy(JToken value, JToken branch)
        {
            if (!(value is JObject o) || !(branch?["properties"] is JObject props)) return false;
            bool any = false;
            foreach (JProperty p in props.Properties())
            {
                JToken c = (p.Value as JObject)?["const"];
                if (c == null) continue;
                any = true;
                if (!o.TryGetValue(p.Name, out JToken v) || !JToken.DeepEquals(v, c)) return false;
            }
            return any;
        }

        /// <summary>A bucket key for uniqueItems: equal JSON values always share a key
        /// (numbers by value, so 1 and 1.0 meet; containers by kind and size, since object
        /// key order does not change equality), and DeepEquals decides inside the bucket.</summary>
        private static string UniqueKey(JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return "n:" + t.Value<double>().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                case JTokenType.String: return "s:" + (string)t;
                case JTokenType.Boolean: return (bool)t ? "b:1" : "b:0";
                case JTokenType.Null: return "z";
                case JTokenType.Array: return "a:" + ((JArray)t).Count;
                case JTokenType.Object: return "o:" + ((JObject)t).Count;
                default: return "x:" + t.Type;
            }
        }

        private static bool HasType(JToken v, string type)
        {
            switch (type)
            {
                case "object": return v.Type == JTokenType.Object;
                case "array": return v.Type == JTokenType.Array;
                case "string": return v.Type == JTokenType.String;
                case "boolean": return v.Type == JTokenType.Boolean;
                case "null": return v.Type == JTokenType.Null;
                case "number": return v.Type == JTokenType.Integer || v.Type == JTokenType.Float;
                case "integer":
                    return v.Type == JTokenType.Integer ||
                           (v.Type == JTokenType.Float && Math.Floor(v.Value<double>()) == v.Value<double>());
                default: return false;
            }
        }

        private static string At(string pointer) => (pointer.Length == 0 ? "/" : pointer) + ": ";
    }
}
