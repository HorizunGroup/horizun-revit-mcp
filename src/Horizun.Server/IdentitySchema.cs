// -----------------------------------------------------------------------------
// Horizun MCP server - the identity fields of a covered writer, in the schema a client READS (not the one tools/list ships).
//
// tools/list is at its size ceiling, so most covered writers do not declare unique_id / unique_ids / expected_category /
// expected_type / identity there. They accept them all the same (UndeclaredArguments lets them through and the add-in
// verifies them before the command runs). This is where that is made visible without a byte more in tools/list: the
// resource horizun://contract/tools/{tool}, the error of a refused call and the schema-help advice all read the schema
// from here, so a client that validates arguments against them sees the fields as declared.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class IdentitySchema
    {
        private static readonly ConcurrentDictionary<string, JToken> Cache = new ConcurrentDictionary<string, JToken>(StringComparer.Ordinal);

        internal const string Summary =
            "Accepts the element-identity fields (identity, unique_id, unique_ids, expected_category, expected_type) on any node that holds element ids, " +
            "even where tools/list does not repeat them: the call is refused, writing nothing, when the model disagrees. " +
            "Only unique_id identifies an element; a category/type alone does not detect a swap inside the same category, and is refused on deletions, in any collaborative (workshared) model, and elsewhere on elements that are not ours (no Horizun mark of this user). See docs/IDENTITY-GUARD.md.";

        /// <summary>The contract's input schema, plus the identity properties when the tool is covered by the identity guard.</summary>
        internal static JToken For(string tool)
        {
            JToken schema = Contract.Find(tool)?.InputSchema;
            if (!(schema is JObject) || !ElementIdentityGuard.Covers(tool)) return schema;
            return Cache.GetOrAdd(tool, _ => Augment((JObject)schema));
        }

        /// <summary>The identity fields a covered writer accepts, as {summary, properties}; null for any other tool.</summary>
        internal static JObject Fields(string tool)
        {
            if (!ElementIdentityGuard.Covers(tool)) return null;
            var schema = (JObject)Augment(new JObject { ["properties"] = new JObject() });
            var props = new JObject();
            foreach (string name in ElementIdentityGuard.AcceptedNames) props[name] = schema["properties"][name].DeepClone();
            return new JObject { ["summary"] = Summary, ["properties"] = props };
        }

        private static JToken Augment(JObject original)
        {
            var schema = (JObject)original.DeepClone();
            var props = schema["properties"] as JObject;
            if (props == null) { props = new JObject(); schema["properties"] = props; }
            void Add(string name, JObject definition) { if (props[name] == null) props[name] = definition; }
            Add("identity", new JObject
            {
                ["type"] = "object",
                ["description"] = "{id_key: unique_id | {unique_id, expected_category, expected_type}} on the node that holds the id key. Every key named is verified."
            });
            Add("unique_id", new JObject { ["type"] = "string", ["description"] = "Compared with the first present id key of the node (docs/IDENTITY-GUARD.md); name the key in identity to choose." });
            Add("unique_ids", new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } });
            Add("expected_category", new JObject { ["type"] = "string", ["description"] = "Category name or BuiltInCategory (OST_Walls). Alone it does not identify an element: send the unique_id too." });
            Add("expected_type", new JObject { ["type"] = "string", ["description"] = "Type name or 'Family: Type'. Alone it does not identify an element: send the unique_id too." });
            string description = (string)schema["description"];
            schema["description"] = string.IsNullOrEmpty(description) ? Summary : description + " " + Summary;
            return schema;
        }
    }
}
