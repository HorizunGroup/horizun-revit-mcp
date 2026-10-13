// -----------------------------------------------------------------------------
// Horizun MCP server tests - original Horizun code.
//
// THE ADVERTISED COPY IS AN ABRIDGEMENT, NEVER A DIFFERENT SCHEMA. tools/list no
// longer repeats a union field's schema inside every oneOf/anyOf/allOf branch
// (Tools.SubtractBranchDuplicates) nor a branch `type` equal to its parent's
// (Tools.DropBranchTypeEqualToParent). These facts are the proof that this is
// only a compression:
//   - every keyword a branch lost is identical in the union beside it (so the
//     full branch can be rebuilt from the advertised document alone);
//   - the advertised schema and the full contract give the SAME verdict on a
//     deterministic corpus of valid and mutated calls;
//   - argument names are identical at every instance location;
//   - the discriminator enums are complete and equal the branch consts;
//   - no boolean or empty schema appears where the contract had a real one
//     (codex-rs sanitize_json_schema coerces a boolean schema to {type:string}).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class AdvertisedSchemaEquivalenceTests
    {
        private static readonly string[] Combinators = { "oneOf", "anyOf", "allOf" };
        private static readonly string[][] Groups =
        {
            new[] { "properties", "patternProperties", "additionalProperties", "required", "propertyNames", "minProperties", "maxProperties" },
            new[] { "items", "prefixItems", "additionalItems", "minItems", "maxItems", "uniqueItems", "contains", "minContains", "maxContains" },
            new[] { "if", "then", "else" }
        };

        private static JObject Full(string tool) => Contract.Find(tool).InputSchema;

        private static JObject Advertised(string tool) =>
            (JObject)Tools.Publish(Tools.Find(tool), true)["inputSchema"];

        /// <summary>The structural steps alone, before the description caps.</summary>
        private static JObject Structural(JObject full)
        {
            var copy = (JObject)full.DeepClone();
            Tools.SubtractBranchDuplicates(copy);
            Tools.DropBranchTypeEqualToParent(copy);
            return copy;
        }

        /// <summary>
        /// What tools/list advertised before the structural steps (c56a742): the description
        /// caps alone on a clone of the contract. Clients have seen this copy; the new one must
        /// not accept or reject anything it did not.
        /// </summary>
        private static JObject PreChange(JObject full)
        {
            var copy = (JObject)full.DeepClone();
            Tools.CompactSchemaNode(copy);
            return copy;
        }

        private static IEnumerable<CommandContract> WithSchemas() => Contract.All.Where(c => c.InputSchema != null);

        [Fact]
        public void Advertised_branches_rehydrate_to_the_full_schema()
        {
            var failures = new List<string>();
            int fieldsChecked = 0;
            foreach (CommandContract c in WithSchemas())
                Rehydrates(c.InputSchema, Structural(c.InputSchema), c.Name + ":", failures, ref fieldsChecked);
            Assert.True(failures.Count == 0, string.Join("\n", failures.Take(25)));
            // MEASURED 2026-09-26: 317 branch fields sit beside a union field of the same name.
            Assert.True(fieldsChecked >= 250, "the walk reached only " + fieldsChecked + " branch fields; it is not looking where the subtraction works");
        }

        private static void Rehydrates(JToken full, JToken adv, string at, List<string> failures, ref int fieldsChecked)
        {
            if (full is JArray fa && adv is JArray aa)
            {
                if (fa.Count != aa.Count) { failures.Add(at + " array length changed"); return; }
                for (int i = 0; i < fa.Count; i++) Rehydrates(fa[i], aa[i], at + "/" + i, failures, ref fieldsChecked);
                return;
            }
            if (!(full is JObject f) || !(adv is JObject a)) return;
            if (f["properties"] is JObject union)
                foreach (string comb in Combinators)
                    if (f[comb] is JArray fbs && a[comb] is JArray abs)
                        for (int i = 0; i < fbs.Count; i++)
                        {
                            if (!(fbs[i] is JObject fb) || !(abs[i] is JObject ab)) continue;
                            string bat = at + "/" + comb + "/" + i;
                            if (fb["type"] != null && ab["type"] == null && !JToken.DeepEquals(fb["type"], f["type"]))
                                failures.Add(bat + " lost a type its parent does not state");
                            if (!(fb["properties"] is JObject fprops)) continue;
                            var aprops = ab["properties"] as JObject;
                            foreach (JProperty fp in fprops.Properties())
                            {
                                JToken ap = aprops?[fp.Name];
                                if (ap == null) { failures.Add(bat + "/properties/" + fp.Name + " was removed"); continue; }
                                if (!(fp.Value is JObject fv) || !(union[fp.Name] is JObject uv)) continue;
                                fieldsChecked++;
                                CheckField(fv, (JObject)ap, uv, bat + "/properties/" + fp.Name, failures);
                            }
                        }
            foreach (JProperty p in f.Properties())
                if (a[p.Name] != null) Rehydrates(p.Value, a[p.Name], at + "/" + p.Name, failures, ref fieldsChecked);
        }

        private static void CheckField(JObject full, JObject adv, JObject union, string at, List<string> failures)
        {
            foreach (JProperty kw in adv.Properties())
            {
                if (full[kw.Name] != null)
                {
                    // A present keyword is the contract's own, or it is a nested schema the
                    // same rules abridged further; the recursion in Rehydrates checks those.
                    if (kw.Value is JObject || kw.Value is JArray) continue;
                    if (!JToken.DeepEquals(full[kw.Name], kw.Value)) failures.Add(at + "." + kw.Name + " differs from the contract");
                }
                else if (!(kw.Name == "type" && JToken.DeepEquals(union["type"], kw.Value)))
                    failures.Add(at + "." + kw.Name + " is not in the contract");
            }
            foreach (JProperty kw in full.Properties())
            {
                if (adv[kw.Name] != null) continue;
                string[] group = Groups.FirstOrDefault(g => g.Contains(kw.Name));
                bool recoverable = group == null
                    ? JToken.DeepEquals(union[kw.Name], kw.Value)
                    : group.All(k => JToken.DeepEquals(full[k], union[k]));
                if (!recoverable) failures.Add(at + "." + kw.Name + " was dropped but the union does not state it");
                if (kw.Name == "type" || kw.Name == "const" || Combinators.Contains(kw.Name))
                    failures.Add(at + "." + kw.Name + " must never be dropped");
            }
        }

        [Fact]
        public void Advertised_schema_gives_the_same_verdicts_as_the_contract()
        {
            var corpus = new List<(string tool, JToken args)>();
            var rng = new Random(7);
            foreach (string file in Directory.GetFiles(Path.Combine(RepoRoot(), "examples"), "*.json", SearchOption.AllDirectories))
            {
                JObject doc;
                try { doc = JObject.Parse(File.ReadAllText(file)); } catch { continue; }
                string tool = (string)doc["tool"];
                if (tool != null && doc["arguments"] != null && Contract.Find(tool)?.InputSchema != null)
                    corpus.Add((tool, doc["arguments"]));
            }
            int examples = corpus.Count;
            foreach (CommandContract c in WithSchemas())
            {
                var bases = new List<JToken>();
                for (int i = 0; i < 12; i++) bases.Add(Gen(c.InputSchema, rng, 0, -1));
                bases.AddRange(PerBranch(c.InputSchema, rng));
                foreach (JToken b in bases)
                {
                    corpus.Add((c.Name, b));
                    foreach (JToken m in Mutations(b, bases, rng)) corpus.Add((c.Name, m));
                }
            }
            Assert.True(examples > 0, "no examples/ payload was found");
            Assert.True(corpus.Count >= 5000, "the corpus has only " + corpus.Count + " instances");

            var disagreements = new List<string>();
            int accepted = 0;
            var schemas = new Dictionary<string, (JObject full, JObject adv, JObject before)>();
            foreach ((string tool, JToken args) in corpus)
            {
                if (!schemas.TryGetValue(tool, out var s)) schemas[tool] = s = (Full(tool), Advertised(tool), PreChange(Full(tool)));
                bool fullOk = ContractSchemaCheck.Validate(args, s.full).Count == 0;
                bool advOk = ContractSchemaCheck.Validate(args, s.adv).Count == 0;
                // The pre-change copy differs from the contract only in capped descriptions, which
                // carry no verdict; checking it anyway pins that the abridgement changed nothing a
                // client that cached the old tools/list would have been told.
                bool beforeOk = ContractSchemaCheck.Validate(args, s.before).Count == 0;
                if (fullOk) accepted++;
                if ((fullOk != advOk || beforeOk != advOk) && disagreements.Count < 10)
                    disagreements.Add(tool + " full=" + fullOk + " advertised=" + advOk + " pre-change=" + beforeOk + " "
                        + args.ToString(Newtonsoft.Json.Formatting.None));
            }
            Assert.True(disagreements.Count == 0, "verdicts differ:\n" + string.Join("\n", disagreements));
            // Both sides must be exercised: a corpus that only fails proves nothing about acceptance.
            Assert.True(accepted >= 500 && accepted <= corpus.Count - 500,
                "the corpus must hold both accepted and rejected calls; accepted " + accepted + " of " + corpus.Count);
        }

        /// <summary>One instance per branch of every discriminated union reachable from the root.</summary>
        private static IEnumerable<JToken> PerBranch(JObject root, Random rng)
        {
            if (root["oneOf"] is JArray top)
                for (int i = 0; i < top.Count; i++) yield return Gen(root, rng, 0, i);
            if (root["properties"] is JObject props)
                foreach (JProperty p in props.Properties())
                    if (p.Value["items"] is JObject items && items["oneOf"] is JArray inner)
                        for (int i = 0; i < inner.Count; i++)
                        {
                            var inst = Gen(root, rng, 0, -1) as JObject ?? new JObject();
                            inst[p.Name] = new JArray(Gen(items, rng, 1, i));
                            yield return inst;
                        }
        }

        private static JToken Gen(JToken schemaToken, Random rng, int depth, int branch)
        {
            if (!(schemaToken is JObject s)) return "x";
            if (s["const"] != null) return s["const"].DeepClone();
            if (s["enum"] is JArray en && en.Count > 0) return en[rng.Next(en.Count)].DeepClone();
            JArray comb = s["oneOf"] as JArray ?? s["anyOf"] as JArray;
            JObject chosen = comb != null && comb.Count > 0
                ? comb[branch >= 0 ? branch : rng.Next(comb.Count)] as JObject
                : null;
            string type = TypeOf(s) ?? (chosen != null ? TypeOf(chosen) : null) ??
                          (s["properties"] != null ? "object" : null);
            if (type == null && chosen != null) return Gen(chosen, rng, depth, -1);
            switch (type)
            {
                case "object":
                    var o = new JObject();
                    var props = new Dictionary<string, JToken>();
                    foreach (JObject src in new[] { s, chosen })
                        if (src?["properties"] is JObject ps)
                            foreach (JProperty p in ps.Properties()) props[p.Name] = p.Value;
                    var required = new HashSet<string>(
                        new[] { s, chosen }.Where(x => x?["required"] is JArray).SelectMany(x => ((JArray)x["required"]).Select(r => (string)r)));
                    foreach (var kv in props)
                        if (required.Contains(kv.Key) || (depth < 2 && rng.NextDouble() < 0.25))
                            o[kv.Key] = Gen(kv.Value, rng, depth + 1, -1);
                    return o;
                case "array":
                    int min = (int?)s["minItems"] ?? 0, max = (int?)s["maxItems"] ?? 2;
                    int n = Math.Min(Math.Max(min, depth < 2 ? 1 : 0), Math.Max(max, min));
                    var arr = new JArray();
                    for (int i = 0; i < n; i++) arr.Add(Gen(s["items"], rng, depth + 1, -1));
                    return arr;
                case "integer":
                    long lo = (long?)s["minimum"] ?? ((long?)s["exclusiveMinimum"] + 1) ?? 1;
                    long hi = (long?)s["maximum"] ?? lo + 5;
                    return Math.Min(lo, hi);
                case "number":
                    double dlo = (double?)s["minimum"] ?? ((double?)s["exclusiveMinimum"] + 0.5) ?? 1.5;
                    return dlo;
                case "boolean": return rng.Next(2) == 0;
                case "null": return JValue.CreateNull();
                default:
                    int len = Math.Max((int?)s["minLength"] ?? 1, 1);
                    return new string('a', len);
            }
        }

        private static string TypeOf(JObject s) =>
            s["type"] is JArray ta ? (string)ta.FirstOrDefault(t => (string)t != "null") : (string)s["type"];

        /// <summary>Drop each field, add an unknown one, change each field's type, break each
        /// string (a wrong const or enum value), and graft a field from another instance.</summary>
        private static IEnumerable<JToken> Mutations(JToken original, List<JToken> siblings, Random rng)
        {
            int objects = Objs(original).Count();
            for (int k = 0; k < objects; k++)
            {
                JObject target = Objs(original).ElementAt(k);
                string[] keys = target.Properties().Select(p => p.Name).ToArray();
                foreach (string key in keys)
                {
                    yield return Mutate(original, k, o => o.Remove(key));
                    yield return Mutate(original, k, o => o[key] = WrongType(o[key]));
                    if (o0(target[key]) is string) yield return Mutate(original, k, o => o[key] = "zz_not_a_value");
                }
                yield return Mutate(original, k, o => o["zz_unknown_field"] = 1);
                JToken donor = siblings[rng.Next(siblings.Count)];
                JObject donorObj = Objs(donor).FirstOrDefault(d => d.Properties().Any(p => target[p.Name] == null));
                JProperty graft = donorObj?.Properties().FirstOrDefault(p => target[p.Name] == null);
                if (graft != null) yield return Mutate(original, k, o => o[graft.Name] = graft.Value.DeepClone());
            }
        }

        private static object o0(JToken t) => t is JValue v ? v.Value : null;

        private static IEnumerable<JObject> Objs(JToken t) =>
            t is JContainer c ? c.DescendantsAndSelf().OfType<JObject>() : Enumerable.Empty<JObject>();

        private static JToken Mutate(JToken original, int objectIndex, Action<JObject> change)
        {
            JToken copy = original.DeepClone();
            change(Objs(copy).ElementAt(objectIndex));
            return copy;
        }

        private static JToken WrongType(JToken v)
        {
            switch (v?.Type)
            {
                case JTokenType.String: return 12345;
                case JTokenType.Integer:
                case JTokenType.Float: return "not a number";
                case JTokenType.Boolean: return "yes";
                case JTokenType.Array: return new JObject { ["x"] = 1 };
                default: return new JArray("x");
            }
        }

        // Names are compared per INSTANCE location: a branch's names merge into the
        // location they validate. Inside one branch a field whose nested object schema
        // equals the union field's is advertised as {"type":"object"} (source_reference in
        // 27 create_elements kinds, MEASURED 2026-09-26), and its nested names live in the
        // union one level up; Advertised_branches_rehydrate_to_the_full_schema proves each
        // such fold is identical to the union, so no name a caller can send disappears.
        [Fact]
        public void Property_names_are_identical_at_every_depth()
        {
            var failures = new List<string>();
            foreach (CommandContract c in WithSchemas())
            {
                var full = new HashSet<string>(StringComparer.Ordinal);
                var adv = new HashSet<string>(StringComparer.Ordinal);
                Names(c.InputSchema, "", full);
                Names(Advertised(c.Name), "", adv);
                foreach (string missing in full.Except(adv)) failures.Add(c.Name + " lost " + missing);
                foreach (string extra in adv.Except(full)) failures.Add(c.Name + " gained " + extra);
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures.Take(25)));
        }

        /// <summary>Argument names by INSTANCE location: a branch adds names at its parent's
        /// location, so a name the union states once and a branch no longer repeats is the
        /// same argument at the same place.</summary>
        private static void Names(JToken schema, string at, HashSet<string> acc)
        {
            if (!(schema is JObject o)) return;
            if (o["properties"] is JObject props)
                foreach (JProperty p in props.Properties()) { acc.Add(at + "/" + p.Name); Names(p.Value, at + "/" + p.Name, acc); }
            if (o["items"] != null) Names(o["items"], at + "/[]", acc);
            if (o["additionalProperties"] is JObject ap) Names(ap, at + "/*", acc);
            foreach (string comb in Combinators)
                if (o[comb] is JArray arr) foreach (JToken b in arr) Names(b, at, acc);
            foreach (string k in new[] { "if", "then", "else", "not" })
                if (o[k] != null) Names(o[k], at, acc);
        }

        [Theory]
        [InlineData("horizun_create_elements", "properties.elements.items.properties.kind.enum", 33)]
        [InlineData("horizun_manage_views", "properties.actions.items.properties.operation.enum", 46)]
        [InlineData("horizun_export", "properties.format.enum", 13)]
        [InlineData("horizun_create_family", "properties.forms.items.properties.kind.enum", 5)]
        public void Discriminator_enums_are_complete_and_equal_the_branch_consts(string tool, string path, int count)
        {
            JToken adv = Advertised(tool).SelectToken(path);
            JToken full = Full(tool).SelectToken(path);
            Assert.NotNull(adv);
            Assert.True(JToken.DeepEquals(full, adv), tool + " " + path + " differs from the contract");
            Assert.Equal(count, ((JArray)adv).Count);

            var failures = new List<string>();
            foreach (JObject node in Advertised(tool).DescendantsAndSelf().OfType<JObject>())
                foreach (string comb in Combinators)
                    if (node[comb] is JArray branches && node["properties"] is JObject props)
                        foreach (JProperty d in props.Properties())
                        {
                            if (!(d.Value["enum"] is JArray values)) continue;
                            var consts = branches.Select(b => b.SelectToken("properties." + d.Name + ".const")).ToList();
                            if (consts.Count < 2 || consts.Any(x => x == null)) continue;
                            var a = new HashSet<string>(values.Select(v => v.ToString()));
                            var b2 = new HashSet<string>(consts.Select(v => v.ToString()));
                            if (!a.SetEquals(b2)) failures.Add(tool + " " + d.Name + ": enum and branch consts differ");
                        }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public void No_boolean_or_empty_subschema_is_emitted()
        {
            var failures = new List<string>();
            foreach (CommandContract c in WithSchemas())
            {
                var full = new Dictionary<string, JToken>(StringComparer.Ordinal);
                Positions(c.InputSchema, "", (p, t) => full[p] = t);
                Positions(Advertised(c.Name), "", (p, t) =>
                {
                    full.TryGetValue(p, out JToken f);
                    bool bad = t is JValue || (t is JObject o && o.Count == 0);
                    bool wasBad = f == null || f is JValue || (f is JObject fo && fo.Count == 0);
                    if (bad && !wasBad) failures.Add(c.Name + p + " is advertised as " + t.ToString(Newtonsoft.Json.Formatting.None));
                    if (System.Text.RegularExpressions.Regex.IsMatch(p, "/properties/[^/]+$") && f is JObject fobj && fobj["type"] != null && (!(t is JObject tobj) || tobj["type"] == null))
                        failures.Add(c.Name + p + " lost its type");
                });
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures.Take(25)));
        }

        [Fact]
        public void Short_forms_replace_every_repeat_and_the_contract_keeps_the_full_text()
        {
            foreach (var kv in Tools.AdvertisedShortForms)
            {
                Assert.True(kv.Value.Length <= Tools.SchemaDescriptionMax, "short form longer than the cap: " + kv.Value);
                int inContract = 0, afterSubtraction = 0, inAdvertised = 0, shortInAdvertised = 0;
                foreach (CommandContract c in WithSchemas())
                {
                    inContract += Count(c.InputSchema, kv.Key);
                    afterSubtraction += Count(Structural(c.InputSchema), kv.Key);
                    JObject adv = Advertised(c.Name);
                    inAdvertised += Count(adv, kv.Key);
                    shortInAdvertised += Count(adv, kv.Value);
                }
                // MEASURED 2026-09-26: the idempotency_key text is repeated 65 times. The
                // 2026-10-10 short forms each replace a text repeated in at least three tools.
                int floor = kv.Key.StartsWith("REQUIRED whenever this call will mutate", StringComparison.Ordinal) ? 65 : 3;
                Assert.True(inContract >= floor, "the contract holds the full text " + inContract + " times (at least " + floor +
                    " expected - did its wording change?): " + kv.Key);
                Assert.Equal(0, inAdvertised);
                // A branch copy identical to its union field was already folded away by the
                // subtraction (MEASURED 2026-09-26: 5 in horizun_document_session); every
                // occurrence that is still advertised shows the short form.
                Assert.Equal(afterSubtraction, shortInAdvertised);
                string resource = (string)McpResources.Read(new JObject { ["uri"] = "horizun://contract/tools" })["contents"][0]["text"];
                Assert.Contains(Newtonsoft.Json.JsonConvert.ToString(kv.Key).Trim('"'), resource);
            }
        }

        private static int Count(JToken schema, string text) =>
            Objs(schema).Count(o => o["description"] is JValue d && d.Type == JTokenType.String && (string)d == text);

        private static void Positions(JToken schema, string ptr, Action<string, JToken> visit)
        {
            visit(ptr, schema);
            if (!(schema is JObject o)) return;
            if (o["properties"] is JObject props)
                foreach (JProperty p in props.Properties()) Positions(p.Value, ptr + "/properties/" + p.Name, visit);
            if (o["items"] != null) Positions(o["items"], ptr + "/items", visit);
            foreach (string comb in Combinators)
                if (o[comb] is JArray arr)
                    for (int i = 0; i < arr.Count; i++) Positions(arr[i], ptr + "/" + comb + "/" + i, visit);
            foreach (string k in new[] { "if", "then", "else", "not" })
                if (o[k] != null) Positions(o[k], ptr + "/" + k, visit);
        }

        private static string RepoRoot([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", ".."));
    }
}
