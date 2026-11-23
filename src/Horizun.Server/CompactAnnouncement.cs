// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// COMPACT ANNOUNCEMENT OF THE FIVE HEAVIEST SCHEMAS (4.0.0, behind a switch).
//
// Five tools carry about 86 KB of the list - create_elements, create_family,
// export, manage_views and plan_annotations - because each is a discriminated
// union whose every variant is spelled out. With HORIZUN_COMPACT_SCHEMAS=1 their
// tools/list entry keeps the description, the discriminator with every value and
// the arguments every call shares; the per-variant fields are left to
// horizun_describe_tool {name, variant} and horizun://contract/tools/{tool}/{variant}.
//
// OFF BY DEFAULT, and that is deliberate: a smaller announcement may raise the
// argument-error rate, so the agent-completion battery measures it first.
//
// WHAT IT NEVER CHANGES: validation. Arguments are still checked against the FULL
// contract (Tools.All -> InputSchema), schema_help still carries the exact branch a
// failed call violated, and the contract hash does not move.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class CompactAnnouncement
    {
        public const string EnvironmentVariable = "HORIZUN_COMPACT_SCHEMAS";

        /// <summary>The five heaviest discriminated schemas (proposal 4.0, section 3.3).</summary>
        public static readonly string[] Heaviest =
        {
            "horizun_create_elements", "horizun_export", "horizun_manage_views",
            "horizun_plan_annotations"
        };

        /// <summary>Read per call, like every other switch of this process.</summary>
        public static bool Enabled
        {
            get
            {
                string v = null;
                try { v = Environment.GetEnvironmentVariable(EnvironmentVariable); } catch { }
                v = v?.Trim().ToLowerInvariant();
                return v == "1" || v == "true" || v == "on" || v == "yes";
            }
        }

        public static bool Applies(string tool) => Enabled && Heaviest.Contains(tool, StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, JObject> Cache =
            new ConcurrentDictionary<string, JObject>(StringComparer.Ordinal);

        /// <summary>The compact advertised schema of one of the five (a copy; callers may not mutate the cache).</summary>
        public static JObject For(string tool, JObject fullSchema)
        {
            if (fullSchema == null) return null;
            return Cache.GetOrAdd(tool, _ => Build(tool, fullSchema));
        }

        internal static JObject Build(string tool, JObject fullSchema)
        {
            var copy = (JObject)Tools.CompactSchema(tool, fullSchema).DeepClone();
            List<VariantSite> sites = ContractVariants.For(tool).ToList();
            int folded = 0;
            // Deepest first, so folding an outer site never strands an inner one.
            foreach (VariantSite site in sites.OrderByDescending(s => s.Segments.Length))
                if (Fold(copy, tool, site)) folded++;

            // Every remaining OPTIONAL argument keeps its NAME, type and enum, and loses its prose and
            // inner structure; required arguments, the discriminators and whatever holds a folded site
            // keep their shape, and the objects inside them (manage_views' actions[]) get the same
            // treatment one level down. describe_tool still serves every word, and validation reads
            // the full contract.
            var keepWhole = new HashSet<string>(sites.Select(s => s.Discriminator), StringComparer.Ordinal);
            foreach (VariantSite s in sites)
                for (int i = 0; i + 1 < s.Segments.Length; i++)
                    if (s.Segments[i] == "properties") keepWhole.Add(s.Segments[i + 1]);
            CompactNode(copy, keepWhole, 0);
            copy["description"] = "Compact announcement: " + Hint(tool, sites) +
                                  (copy["description"] != null && copy["description"].Type == JTokenType.String
                                      ? " " + (string)copy["description"] : "");
            return copy;
        }

        private static string Hint(string tool, List<VariantSite> sites)
        {
            string head = sites.Count == 0 ? "optional arguments carry no description here" :
                "per-variant fields are not listed (" + string.Join(", ", sites.Select(s => s.Discriminator).Distinct()) + ")";
            return head + "; horizun_describe_tool {\"name\":\"" + tool + "\"" +
                   (sites.Count == 0 ? "" : ",\"variant\":\"<value>\"") + "} gives the exact schema, which is what " +
                   "arguments are validated against.";
        }

        private static bool Fold(JObject root, string tool, VariantSite site)
        {
            JToken cur = root;
            foreach (string seg in site.Segments)
            {
                if (cur is JObject o) cur = o[seg];
                else if (cur is JArray a && int.TryParse(seg, out int i) && i >= 0 && i < a.Count) cur = a[i];
                else return false;
                if (cur == null) return false;
            }
            if (!(cur is JObject node) || !(node[site.Combinator] is JArray)) return false;

            node.Remove(site.Combinator);
            var keep = new HashSet<string>(StringComparer.Ordinal) { site.Discriminator };
            if (node["required"] is JArray req)
                foreach (JToken r in req) if (r.Type == JTokenType.String) keep.Add((string)r);
            if (node["properties"] is JObject props)
            {
                foreach (JProperty p in props.Properties().ToList())
                    if (!keep.Contains(p.Name)) p.Remove();
                if (props[site.Discriminator] is JObject d)
                {
                    d["enum"] = new JArray(site.Values);
                    d["description"] = "Selects the variant. Its fields: horizun_describe_tool {\"name\":\"" + tool +
                                       "\",\"variant\":\"<this value>\"}.";
                }
            }
            // The fields that were folded away are still accepted: a client that validates
            // against this copy must not refuse a call the contract admits.
            if (node["additionalProperties"] != null) node.Remove("additionalProperties");
            if (node["unevaluatedProperties"] != null) node.Remove("unevaluatedProperties");
            return true;
        }

        private static void CompactNode(JObject node, HashSet<string> keepWhole, int depth)
        {
            if (!(node["properties"] is JObject props)) return;
            var required = new HashSet<string>(((node["required"] as JArray) ?? new JArray())
                .Where(t => t.Type == JTokenType.String).Select(t => (string)t), StringComparer.Ordinal);
            foreach (JProperty p in props.Properties())
            {
                if (!(p.Value is JObject arg)) continue;
                if (required.Contains(p.Name) || keepWhole.Contains(p.Name))
                {
                    if (depth >= 3) continue;
                    JObject inner = (arg["items"] as JObject) ?? arg;
                    CompactNode(inner, keepWhole, depth + 1);
                }
                else p.Value = Minimal(arg);
            }
        }

        /// <summary>Name-level announcement of one argument: type, enum and a primitive item type.</summary>
        internal static JObject Minimal(JObject arg)
        {
            var m = new JObject();
            if (arg["type"] != null) m["type"] = arg["type"].DeepClone();
            if (arg["enum"] != null) m["enum"] = arg["enum"].DeepClone();
            if (arg["const"] != null) m["const"] = arg["const"].DeepClone();
            if (arg["items"] is JObject items && items["type"] != null && items["type"].Type == JTokenType.String &&
                (string)items["type"] != "object" && (string)items["type"] != "array")
            {
                var it = new JObject { ["type"] = items["type"].DeepClone() };
                if (items["enum"] != null) it["enum"] = items["enum"].DeepClone();
                m["items"] = it;
            }
            if (m.Count == 0)
            {
                // No type to keep (a bare combinator): keep it whole rather than announce "anything".
                var whole = (JObject)arg.DeepClone();
                StripProse(whole);
                return whole;
            }
            return m;
        }

        // Schema nodes only: a "properties" map is walked by value, so an ARGUMENT that happens
        // to be called "description" or "examples" is never removed.
        private static void StripProse(JToken node)
        {
            if (node is JObject o)
            {
                if (o["description"] != null && o["description"].Type == JTokenType.String) o.Remove("description");
                if (o["examples"] is JArray) o.Remove("examples");
                foreach (JProperty p in o.Properties().ToList())
                {
                    if ((p.Name == "properties" || p.Name == "patternProperties") && p.Value is JObject props)
                        foreach (JProperty arg in props.Properties()) StripProse(arg.Value);
                    else if (p.Name != "enum" && p.Name != "const" && p.Name != "default")
                        StripProse(p.Value);
                }
            }
            else if (node is JArray a)
                foreach (JToken item in a) StripProse(item);
        }
    }
}
