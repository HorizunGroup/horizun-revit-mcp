// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// HORIZUN_SCHEMA_MODE=compact (3.1). The third way tools/list can look.
//
//   full    every reachable tool, abridged schema (the 2.x/3.0 surface, byte for byte);
//   facade  horizun_health + list/describe/call (SchemaFacade.cs);
//   compact the SAME tools as full, each one SHORT: a description of at most 200 characters and
//           only the arguments a model uses almost every time.
//
// WHY. Measured on 3.0.6 (safe_write, HORIZUN_TOOLSETS=local): tools/list weighs ~99 400 B
// (~24 800 tokens); horizun_create_elements alone is 31 838 characters and horizun_query_model has
// 33 arguments. A small local model (qwen3:8b) CHOSE the right tool and then built its arguments
// wrong from schemas that large. The toolset still decides WHAT appears; this mode decides HOW it
// looks. It combines with any HORIZUN_TOOLSETS.
//
// WHAT IT DOES NOT CHANGE. Nothing about acceptance:
//   - the call is validated exactly as in full: against the COMPLETE contract
//     (Tool.InputSchema, ToolInputRules, the add-in's parsers). An argument that the compact
//     schema omits and the contract has is ACCEPTED; an invalid one gets the same refusal and the
//     same schema_help (SchemaHelp reads Contract.Find(tool).InputSchema, not this copy);
//   - dry_run, confirmation_token, idempotency_key and the re-read of every write;
//   - Contract.Hash. Compact is server-only and has its own identity (SchemaCompact.Hash),
//     reported beside it in horizun_health.schema_mode and horizun://build/identity.
//
// The compact schema is therefore a LOOSER, SMALLER view of the contract, never a different one:
// no argument is invented, no enum is shortened, every `required` is kept, and
// additionalProperties:false is dropped so a client that validates the copy does not refuse an
// argument the server accepts. What a tool shows is a table (SchemaCompactSpecs.cs), one row per
// tool, each argument with the reason it is there.
//
// COMPACT READS. In this mode only, a read that has a short response mode gets it when the caller
// did not say (ReadDefaults). A write is never touched.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class SchemaCompact
    {
        /// <summary>The longest description a compact tool carries, closing sentence included.</summary>
        public const int DescriptionMax = 200;

        /// <summary>Longest description of a top-level argument that has no hand-written note.</summary>
        internal const int ArgumentDescriptionMax = 100;

        /// <summary>Longest description of anything nested inside an argument that has no note.</summary>
        internal const int NestedDescriptionMax = 60;

        /// <summary>A tool without a table row shows its required arguments, the flow ones and this many more.</summary>
        internal const int GenericExtraArguments = 6;

        /// <summary>An object nested inside an argument shows at most this many fields when no row picks them.</summary>
        internal const int NestedFieldMax = 8;

        /// <summary>
        /// The arguments that make a write SAFE, kept on every tool that has them: which document,
        /// the rehearsal switch, the token that binds apply to rehearsal, the key that makes a retry
        /// harmless. Losing one from the schema would hide the flow the instructions describe.
        /// </summary>
        internal static readonly string[] FlowArguments =
            { "target_document", "target_document_title", "dry_run", "confirmation_token", "idempotency_key" };

        /// <summary>
        /// Reads that default to the server's short response mode in compact mode. ONLY reads that
        /// already have such a mode in the contract: nothing is invented here, and a read whose short
        /// mode changes WHAT is returned rather than HOW MUCH (model_scan summary samples three
        /// items per bucket) is left to the caller. horizun_list_elements, get_document_info and
        /// horizun_health have no short mode in the contract, so they are not here.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, string> ReadDefaults =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // compact = lean row fields + compact parameters, coverage and identity kept
                // (QueryResponseOptions.Prepare). It only fills fields the caller left unset.
                { "horizun_query_model", "compact" }
            };

        private static string Suffix(string tool) => "Full schema: horizun://contract/tools/" + tool;

        // ---- tools/list -----------------------------------------------------------------------

        /// <summary>What tools/list answers in compact mode: Tools.Advertisable(), each published short.</summary>
        public static JArray List(bool advertiseTaskSupport)
        {
            var arr = new JArray();
            foreach (ToolDef t in Tools.Advertisable()) arr.Add(Publish(t, advertiseTaskSupport));
            return arr;
        }

        /// <summary>
        /// One entry as full publishes it - name, title, outputSchema, annotations, execution, _meta
        /// untouched - with the description and inputSchema replaced by their compact form.
        /// </summary>
        internal static JObject Publish(ToolDef t, bool advertiseTaskSupport)
        {
            JObject published = Tools.Publish(t, advertiseTaskSupport);
            published["description"] = DescriptionOf(t);
            published["inputSchema"] = SchemaOf(t).DeepClone();
            return published;
        }

        // ---- reads ----------------------------------------------------------------------------

        /// <summary>
        /// The arguments to run <paramref name="tool"/> with: in compact mode, a read with a short
        /// mode that was not given one gets it. Anything else returns <paramref name="args"/> itself.
        /// The caller's object is never modified.
        /// </summary>
        internal static JObject ApplyReadDefaults(string tool, JObject args, SchemaSurface mode)
        {
            if (mode != SchemaSurface.Compact || args == null || tool == null) return args;
            if (!ReadDefaults.TryGetValue(tool, out string value)) return args;
            if (args.Property("response_mode") != null) return args;     // the caller chose; respected
            var copy = (JObject)args.DeepClone();
            copy["response_mode"] = value;
            return copy;
        }

        // ---- identity -------------------------------------------------------------------------

        private static readonly Lazy<string> _hash = new Lazy<string>(() =>
        {
            var all = new JArray(Tools.All.Select(t => Publish(t, true)));
            string text = all.ToString(Formatting.None) + "\n" + ServerInstructions.CompactHead;
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)), 0, 12)
                                   .Replace("-", "").ToLowerInvariant();
        });

        /// <summary>
        /// Compact's own identity: a hash of every tool as compact publishes it (whatever the
        /// permission posture) plus the compact instructions, in the format of Contract.Hash. NOT
        /// folded into Contract.Hash - the add-in neither knows nor needs it.
        /// </summary>
        public static string Hash => _hash.Value;

        // ---- description ------------------------------------------------------------------------

        private static readonly ConcurrentDictionary<string, string> Descriptions =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        internal static string DescriptionOf(ToolDef t) => Descriptions.GetOrAdd(t.Name, _ =>
        {
            string suffix = Suffix(t.Name);
            CompactSpec spec = SchemaCompactSpecs.Find(t.Name);
            // Written by hand where there is a row; otherwise a deterministic cut of the contract's
            // own text (the same cut horizun_list_tools uses), short enough to leave room for the suffix.
            string body = spec != null ? spec.Description : SchemaFacade.Summary(t.Description, DescriptionMax - suffix.Length - 1);
            if (string.IsNullOrWhiteSpace(body)) body = "Horizun tool " + t.Name + ".";
            return body.TrimEnd() + " " + suffix;
        });

        // ---- schema -------------------------------------------------------------------------------

        private static readonly ConcurrentDictionary<string, JObject> Schemas =
            new ConcurrentDictionary<string, JObject>(StringComparer.Ordinal);

        internal static JObject SchemaOf(ToolDef t) => Schemas.GetOrAdd(t.Name, _ => Build(t));

        private static JObject Build(ToolDef t)
        {
            JObject full = t.InputSchema;
            var result = new JObject { ["type"] = "object" };
            if (full == null) { result["properties"] = new JObject(); return result; }

            var fullProps = full["properties"] as JObject ?? new JObject();
            CompactSpec spec = SchemaCompactSpecs.Find(t.Name);
            var required = (full["required"] as JArray ?? new JArray()).Select(x => (string)x).ToList();

            var keep = new HashSet<string>(required, StringComparer.Ordinal);
            foreach (string flow in FlowArguments) if (fullProps[flow] != null) keep.Add(flow);
            if (spec != null)
                foreach (CompactArg a in spec.Args) if (fullProps[a.Name] != null) keep.Add(a.Name);
            if (spec == null)
            {
                int extra = 0;
                foreach (JProperty p in fullProps.Properties())
                {
                    if (keep.Contains(p.Name)) continue;
                    if (extra++ >= GenericExtraArguments) break;
                    keep.Add(p.Name);
                }
            }

            var props = new JObject();
            foreach (JProperty p in fullProps.Properties())
            {
                if (!keep.Contains(p.Name)) continue;
                CompactArg arg = spec?.Args.FirstOrDefault(a => a.Name == p.Name);
                JToken schema = arg?.Schema != null ? arg.Schema.DeepClone() : p.Value.DeepClone();
                if (arg?.Schema == null) Shrink(schema, 0);

                if (spec != null && spec.ItemArgs.TryGetValue(p.Name, out CompactArg[] picks) &&
                    schema is JObject arr && arr["items"] is JObject items && p.Value["items"] is JObject fullItems)
                    PickItemFields(items, fullItems, picks);

                string note = arg?.Note ?? (arg?.Schema != null ? null : StandardNote(p.Name, required.Contains(p.Name)));
                if (note != null && schema is JObject so) SetNote(so, note);
                // The mode applies this default to a read that did not choose one (ReadDefaults):
                // say so, instead of advertising the contract's default of "full".
                if (p.Name == "response_mode" && ReadDefaults.TryGetValue(t.Name, out string readDefault) && schema is JObject rm)
                    rm["default"] = readDefault;
                props[p.Name] = schema;
            }

            result["properties"] = props;
            if (required.Count > 0) result["required"] = new JArray(required);
            return result;
        }

        /// <summary>
        /// Replace an array item's fields by the picked ones (plus whatever the item REQUIRES, always),
        /// in the contract's own order. The discriminated oneOf/anyOf/allOf is dropped with the
        /// fields it restated: the server validates the full contract.
        /// </summary>
        private static void PickItemFields(JObject items, JObject fullItems, CompactArg[] picks)
        {
            var fullProps = fullItems["properties"] as JObject;
            if (fullProps == null) return;
            var want = new HashSet<string>(picks.Select(p => p.Name), StringComparer.Ordinal);
            if (fullItems["required"] is JArray req) foreach (JToken r in req) want.Add((string)r);

            var kept = new JObject();
            foreach (JProperty p in fullProps.Properties())
            {
                if (!want.Contains(p.Name)) continue;
                CompactArg pick = picks.FirstOrDefault(a => a.Name == p.Name);
                JToken schema = pick?.Schema != null ? pick.Schema.DeepClone() : p.Value.DeepClone();
                if (pick?.Schema == null) Shrink(schema, 1);
                if (pick?.Note != null && schema is JObject so) SetNote(so, pick.Note);
                kept[p.Name] = schema;
            }
            items["properties"] = kept;
            foreach (string c in new[] { "oneOf", "anyOf", "allOf" }) items.Remove(c);
            if (fullItems["required"] is JArray) items["required"] = fullItems["required"].DeepClone();
            if (items["type"] == null) items["type"] = "object";
        }

        /// <summary>A note of "" means: say nothing - the name and type are enough.</summary>
        private static void SetNote(JObject schema, string note)
        {
            if (note.Length == 0) schema.Remove("description");
            else schema["description"] = note;
        }

        // The flow arguments are explained ONCE, in the server instructions (health first;
        // rehearse, then apply with the same request + dry_run=false + token + new idempotency_key).
        // Here they are one short line each, because a write tool repeats them.
        private static string StandardNote(string name, bool isRequired)
        {
            switch (name)
            {
                case "target_document":
                    return isRequired ? "Document title (see horizun_health)." : "Document title (see horizun_health); omit for the active one.";
                case "target_document_title":
                    return "Document title (see horizun_health); omit for the active one.";
                case "dry_run":
                    return "";   // the instructions explain the flow once; the name and the default say the rest
                case "confirmation_token":
                    return "";
                case "idempotency_key":
                    return "";
                default:
                    return null;
            }
        }

        // ---- generic shrink -----------------------------------------------------------------------

        /// <summary>
        /// Make a schema node smaller without changing what it accepts beyond loosening it:
        /// descriptions cut to a sentence, examples and titles dropped, additionalProperties:false
        /// dropped (the server accepts every contract argument), the discriminated union of an object
        /// that already lists its fields dropped, and an over-wide nested object trimmed to its
        /// required fields plus the first few. Property KEYS that survive keep their type.
        /// </summary>
        private static void Shrink(JToken node, int depth)
        {
            if (node is JArray a) { foreach (JToken x in a) Shrink(x, depth); return; }
            if (!(node is JObject o)) return;

            foreach (string k in new[] { "examples", "$comment", "title" }) o.Remove(k);
            if (o["additionalProperties"] != null && o["additionalProperties"].Type == JTokenType.Boolean &&
                !(bool)o["additionalProperties"])
                o.Remove("additionalProperties");
            // Size limits are the server's to enforce (it validates the full contract). Only a short
            // fixed tuple - a point, a pair - tells a model the SHAPE, so only that bound stays.
            o.Remove("minLength"); o.Remove("maxLength");
            if (o["type"] is JValue tv && tv.Type == JTokenType.String && (string)tv == "array")
            {
                int max = o["maxItems"] != null ? (int)o["maxItems"] : int.MaxValue;
                if (max > 3) { o.Remove("maxItems"); o.Remove("minItems"); }
            }

            JObject props = o["properties"] as JObject;
            if (props != null)
            {
                foreach (string c in new[] { "oneOf", "anyOf", "allOf" }) o.Remove(c);
                if (o["type"] == null) o["type"] = "object";
                if (depth >= 1 && props.Count > NestedFieldMax)
                {
                    var need = new HashSet<string>((o["required"] as JArray ?? new JArray()).Select(x => (string)x));
                    var trimmed = new JObject();
                    int n = 0;
                    foreach (JProperty p in props.Properties())
                        if (need.Contains(p.Name) || n++ < NestedFieldMax) trimmed[p.Name] = p.Value;
                    o["properties"] = props = trimmed;
                }
                foreach (JProperty p in props.Properties()) Shrink(p.Value, depth + 1);
            }

            if (o["description"] is JValue d && d.Type == JTokenType.String)
            {
                // Deeper than an argument's own fields a description is dropped: the field name and type say
                // what a small model needs, and the full schema is one pointer away.
                if (depth >= 2) o.Remove("description");
                else o["description"] = SchemaFacade.Summary((string)d, depth == 0 ? ArgumentDescriptionMax : NestedDescriptionMax);
            }
            if (o["items"] != null) Shrink(o["items"], depth + 1);
            foreach (string c in new[] { "oneOf", "anyOf", "allOf" })
                if (o[c] is JArray branches) foreach (JToken b in branches) Shrink(b, depth + 1);
        }
    }

    /// <summary>One argument (or one field of an array's items) that compact shows.</summary>
    internal sealed class CompactArg
    {
        public string Name;
        /// <summary>WHY it is shown. Documentation of the table; never published.</summary>
        public string Why;
        /// <summary>The description published instead of the contract's, when the contract's is too long or misses a trap.</summary>
        public string Note;
        /// <summary>The whole schema published instead of the contract's, when the contract's is a union a small model cannot use.</summary>
        public JObject Schema;
    }

    /// <summary>One row of the compact table: the hand-written description and the common arguments.</summary>
    internal sealed class CompactSpec
    {
        public string Description;
        public CompactArg[] Args = new CompactArg[0];
        /// <summary>For an array argument: the fields of its items that are shown.</summary>
        public Dictionary<string, CompactArg[]> ItemArgs = new Dictionary<string, CompactArg[]>(StringComparer.Ordinal);
    }
}
