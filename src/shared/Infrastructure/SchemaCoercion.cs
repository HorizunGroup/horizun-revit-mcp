// -----------------------------------------------------------------------------
// Horizun hardening layer — NEW FILE (added to the rvt-mcp base by Horizun).
// Apache-2.0 (see LICENSE); this file is an original Horizun contribution.
//
// (D) JSON contract robustness ("tolerant reader").
//
// LLM clients routinely send params that are semantically right but the wrong
// JSON shape: element_id as the string "347912" when the schema says integer,
// "true" instead of true, or elementId (camelCase) when the handler declares
// element_id (snake_case). The base SchemaValidator correctly REJECTS these —
// but rejecting a call that everyone can see was meant to succeed just burns a
// round-trip. This pass repairs the two safe, unambiguous classes of mismatch
// BEFORE validation, so the handler receives clean, correctly-typed params:
//
//   1. Key aliasing  — when a declared property is absent but exactly one param
//      key normalizes to it (case- and underscore-insensitive), rename it to the
//      canonical name. Handles camelCase <-> snake_case and stray casing.
//   2. Type coercion — when the schema declares number/integer/boolean and the
//      value is a string that parses cleanly to that type, replace it with the
//      typed token. Also applied element-wise to typed arrays.
//
// Both rules are deliberately conservative: coercion fires ONLY where the schema
// explicitly asks for a non-string type, and aliasing fires ONLY when the
// canonical key is missing and the match is unambiguous — so it can never change
// the meaning of a value the caller actually intended. Anything it does not
// understand is passed through untouched for the validator to judge.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    public static class SchemaCoercion
    {
        /// <summary>
        /// Return a params JSON string with safe key-aliasing and type-coercion
        /// applied against <paramref name="schemaJson"/>. Returns the original
        /// string unchanged when the schema is empty/unusable, the params are not
        /// a JSON object, or nothing needed fixing (so callers can cheaply detect
        /// "no change" by reference/equality if they wish).
        /// </summary>
        public static string Coerce(string schemaJson, string paramsJson)
        {
            if (string.IsNullOrWhiteSpace(schemaJson) || schemaJson.Trim() == "{}")
                return paramsJson;

            JObject schema;
            try { schema = JObject.Parse(schemaJson); }
            catch { return paramsJson; }

            if (!(schema["properties"] is JObject properties) || properties.Count == 0)
                return paramsJson;

            if (string.IsNullOrWhiteSpace(paramsJson))
                return paramsJson;

            JObject parameters;
            try { parameters = JObject.Parse(paramsJson); }
            catch { return paramsJson; } // not an object → let the validator explain

            bool changed = false;

            changed |= AliasKeys(properties, parameters);
            changed |= CoerceTypes(properties, parameters);

            return changed ? parameters.ToString(Formatting.None) : paramsJson;
        }

        // --- 1. Key aliasing --------------------------------------------------

        private static bool AliasKeys(JObject properties, JObject parameters)
        {
            // Map normalized-name -> canonical property name.
            var canonicalByNorm = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in properties.Properties())
            {
                var norm = Normalize(prop.Name);
                if (!canonicalByNorm.ContainsKey(norm))
                    canonicalByNorm[norm] = prop.Name;
            }

            // Snapshot current param keys (we mutate the object while iterating).
            var paramKeys = new List<string>();
            foreach (var p in parameters.Properties())
                paramKeys.Add(p.Name);

            bool changed = false;
            foreach (var key in paramKeys)
            {
                // Already a declared property → leave it alone.
                if (properties[key] != null) continue;

                var norm = Normalize(key);
                if (!canonicalByNorm.TryGetValue(norm, out var canonical)) continue;
                if (string.Equals(canonical, key, StringComparison.Ordinal)) continue;

                // Only rename when the canonical slot is free — never overwrite an
                // explicit value the caller already provided under the right name.
                if (parameters[canonical] != null) continue;

                parameters[canonical] = parameters[key];
                parameters.Remove(key);
                changed = true;
            }
            return changed;
        }

        private static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (c == '_' || c == '-' || c == ' ') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        // --- 2. Type coercion -------------------------------------------------

        private static bool CoerceTypes(JObject properties, JObject parameters)
        {
            bool changed = false;
            foreach (var prop in properties.Properties())
            {
                var name = prop.Name;
                var value = parameters[name];
                if (value == null) continue;
                if (!(prop.Value is JObject propSchema)) continue;

                var expected = propSchema.Value<string>("type");
                if (string.IsNullOrEmpty(expected)) continue;

                if (expected == "array")
                {
                    if (value.Type != JTokenType.Array) continue;
                    var itemType = (propSchema["items"] as JObject)?.Value<string>("type");
                    if (string.IsNullOrEmpty(itemType)) continue;
                    var arr = (JArray)value;
                    for (int i = 0; i < arr.Count; i++)
                    {
                        if (TryCoerceScalar(arr[i], itemType, out var coerced))
                        {
                            arr[i] = coerced;
                            changed = true;
                        }
                    }
                    continue;
                }

                if (TryCoerceScalar(value, expected, out var newVal))
                {
                    parameters[name] = newVal;
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>
        /// Coerce a single string token to the expected scalar type when it parses
        /// cleanly. Returns false (and leaves <paramref name="coerced"/> null) when
        /// the token is not a string, already matches, or does not parse — i.e. we
        /// only ever turn a string into the type the schema explicitly asked for.
        /// </summary>
        private static bool TryCoerceScalar(JToken token, string expected, out JToken coerced)
        {
            coerced = null;
            if (token == null || token.Type != JTokenType.String)
                return false;

            var s = token.Value<string>();
            if (s == null) return false;
            var t = s.Trim();
            if (t.Length == 0) return false;

            switch (expected)
            {
                case "integer":
                    if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    {
                        coerced = new JValue(l);
                        return true;
                    }
                    return false;

                case "number":
                    if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        coerced = new JValue(d);
                        return true;
                    }
                    return false;

                case "boolean":
                    if (bool.TryParse(t, out var b))
                    {
                        coerced = new JValue(b);
                        return true;
                    }
                    return false;

                default:
                    return false; // strings, objects, nulls, etc. are left untouched
            }
        }
    }
}
