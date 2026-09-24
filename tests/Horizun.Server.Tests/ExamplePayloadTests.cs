// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// EVERY PAYLOAD UNDER examples/ IS A CALL THE CURRENT CONTRACT ACCEPTS.
//
// An example is documentation a person copies. The failure mode of documentation is
// silent drift: a property renamed, an enum narrowed, a field made required - and the
// page keeps showing the old shape, which the server now refuses. So every example
// file declares the tool it calls (`tool`) and carries the call itself (`arguments`),
// and this test validates the arguments against THAT tool's InputSchema, taken from
// the same Contract.cs the server answers tools/list with. Rename a property and the
// example fails here, not in a user's first call.
//
// The validator is deliberately strict about itself: it understands exactly the JSON
// Schema keywords the contract uses, and a keyword it does not understand is a
// failure rather than a silent pass. A validator that skips what it cannot read
// reports "valid" for the very schemas it never checked.
//
// Beyond the schema, the host-resident examples are RUN where they can be run without
// a file system of their own: the project-context draft is rehearsed and must not be
// invalid or self-contradictory, and the container name must compose and validate.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Horizun.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ExamplePayloadTests
    {
        /// <summary>The top-level keys an example call file may carry. Anything else is a typo.</summary>
        private static readonly HashSet<string> CallKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "tool", "title", "about", "next", "files", "arguments"
        };

        /// <summary>Folders the examples must cover (the discipline set asked for in the brief).</summary>
        private static readonly string[] RequiredExamples =
        {
            "iso19650-startup/01-questions.json",
            "iso19650-startup/02-draft-rehearsal.json",
            "information-containers/01-name.json",
            "information-containers/02-stamp.json",
            "information-containers/03-inspect.json",
            "information-containers/04-transition.json",
            "ifc-delivery/deliver-ifc.json",
            "disciplines/architecture-walls-and-slab.json",
            "disciplines/structure-plan-reinforcement.json",
            "disciplines/mep-ducts.json",
            "disciplines/documentation-export-pdf-container.json"
        };

        // ---- discovery ------------------------------------------------------------------

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "examples")) &&
                    File.Exists(Path.Combine(d.FullName, "src", "Horizun.Contracts", "Contract.cs")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
        }

        private static string ExamplesRoot() => Path.Combine(RepoRoot(), "examples");

        private static string Relative(string path) =>
            Path.GetRelativePath(ExamplesRoot(), path).Replace('\\', '/');

        private static List<string> JsonFiles() =>
            Directory.GetFiles(ExamplesRoot(), "*.json", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal).ToList();

        private static JObject Load(string path)
        {
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path)))
                   { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
            {
                JToken t = JToken.ReadFrom(reader);
                Assert.True(t is JObject, Relative(path) + " is not a JSON object");
                return (JObject)t;
            }
        }

        private static List<(string Path, JObject Doc)> Calls() =>
            JsonFiles().Select(p => (p, Load(p))).Where(x => x.Item2["tool"] != null).ToList();

        // ---- the contract ---------------------------------------------------------------

        [Fact]
        public void Every_required_example_exists()
        {
            foreach (string rel in RequiredExamples)
                Assert.True(File.Exists(Path.Combine(ExamplesRoot(), rel)), "missing example: examples/" + rel);
        }

        [Fact]
        public void Every_json_under_examples_is_either_a_tool_call_or_a_declared_data_document()
        {
            foreach (string path in JsonFiles())
            {
                JObject doc = Load(path);
                if (doc["tool"] != null) continue;
                bool projectContext = doc["schema_version"] != null && doc["project"] != null;
                bool horizunDocument = doc["schema"] is JValue s && s.Type == JTokenType.String &&
                                       ((string)s).StartsWith("horizun.", StringComparison.Ordinal);
                Assert.True(projectContext || horizunDocument,
                    "examples/" + Relative(path) + " declares no `tool` and is not a known data document " +
                    "(a project-context.json or a horizun.* document). A payload nobody validates is how an " +
                    "example drifts: add `tool` + `arguments`, or make it a declared document.");
            }
        }

        [Fact]
        public void Every_example_call_is_well_formed_and_bilingual()
        {
            List<(string Path, JObject Doc)> calls = Calls();
            Assert.True(calls.Count >= RequiredExamples.Length, "found only " + calls.Count + " example calls");
            foreach (var (path, doc) in calls)
            {
                string rel = Relative(path);
                foreach (JProperty p in doc.Properties())
                    Assert.True(CallKeys.Contains(p.Name), "examples/" + rel + ": unknown top-level key '" + p.Name + "'");
                Assert.Equal(JTokenType.String, doc["tool"].Type);
                Assert.True(doc["arguments"] is JObject, "examples/" + rel + ": `arguments` must be an object");
                foreach (string block in new[] { "title", "about" })
                {
                    Assert.True(doc[block] is JObject, "examples/" + rel + ": `" + block + "` must be {en, es}");
                    foreach (string lang in new[] { "en", "es" })
                        Assert.False(string.IsNullOrWhiteSpace((string)doc[block][lang]),
                            "examples/" + rel + ": `" + block + "." + lang + "` is empty");
                }
                string dir = Path.GetDirectoryName(path);
                if (doc["next"] != null)
                    Assert.True(File.Exists(Path.Combine(dir, (string)doc["next"])),
                        "examples/" + rel + ": `next` names a file that does not exist: " + doc["next"]);
                if (doc["files"] is JArray files)
                    foreach (JToken f in files)
                        Assert.True(File.Exists(Path.Combine(dir, (string)f)),
                            "examples/" + rel + ": `files` names a file that does not exist: " + f);
            }
        }

        [Fact]
        public void Every_example_call_validates_against_the_input_schema_of_the_tool_it_declares()
        {
            var byName = Contract.All.ToDictionary(c => c.Name, StringComparer.Ordinal);
            var failures = new List<string>();
            foreach (var (path, doc) in Calls())
            {
                string tool = (string)doc["tool"];
                if (!byName.TryGetValue(tool, out CommandContract contract))
                {
                    failures.Add("examples/" + Relative(path) + ": `tool` names no contract: '" + tool + "'");
                    continue;
                }
                var errors = new List<string>();
                SchemaValidator.Validate(doc["arguments"], contract.InputSchema, "", errors);
                foreach (string e in errors) failures.Add("examples/" + Relative(path) + " (" + tool + "): " + e);
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        [Fact]
        public void Examples_carry_no_personal_paths_and_only_the_demo_root()
        {
            var windowsPath = new Regex(@"(?i)\b[A-Z]:[\\/][^""\s]*");
            foreach (string path in Directory.GetFiles(ExamplesRoot(), "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".json" && ext != ".ids" && ext != ".txt" && ext != ".md") continue;
                string text = File.ReadAllText(path);
                Assert.DoesNotMatch(new Regex(@"(?i)[A-Z]:[\\/]+Users[\\/]"), text);
                if (ext == ".md") continue;   // prose may name %USERPROFILE%-style placeholders
                foreach (Match m in windowsPath.Matches(text))
                    Assert.True(m.Value.StartsWith("C:/proyectos/demo/", StringComparison.Ordinal),
                        "examples/" + Relative(path) + ": absolute path outside C:/proyectos/demo/: " + m.Value);
            }
        }

        [Fact]
        public void The_examples_folder_is_not_ignored_by_git()
        {
            string gitignore = Path.Combine(RepoRoot(), ".gitignore");
            if (!File.Exists(gitignore)) return;
            foreach (string raw in File.ReadAllLines(gitignore))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                string bare = line.TrimStart('/');
                // Only named local artefacts of the QA/QC template may be ignored under examples/.
                if (bare.StartsWith("examples/qaqc-report-template", StringComparison.Ordinal)) continue;
                Assert.False(bare == "examples" || bare == "examples/" || bare.StartsWith("examples/", StringComparison.Ordinal) ||
                             bare == "*.json" || bare == "*.ids",
                    ".gitignore line '" + line + "' would hide example payloads");
            }
        }

        // ---- the host-resident examples, run ---------------------------------------------

        [Fact]
        public void The_project_context_draft_examples_rehearse_to_a_valid_coherent_document()
        {
            foreach (var (path, doc) in Calls().Where(c => (string)c.Doc["tool"] == "horizun_project_context"))
            {
                var args = (JObject)doc["arguments"].DeepClone();
                string op = (string)args["operation"];
                if (op != "draft" && op != "questions") continue;
                // The example's path is a demo path on a machine that is not this one: rehearse
                // without it, so the draft starts from an empty context exactly as it would there.
                args.Remove("path");
                args["dry_run"] = true;
                if (op == "draft") args.Remove("overwrite");
                JObject result = ProjectContext.Handle(args, CancellationToken.None);
                if (op == "questions")
                {
                    Assert.True(result.ToString(Formatting.None).Contains("/project/code"),
                        "examples/" + Relative(path) + ": questions did not start from the project code");
                    continue;
                }
                string state = (string)result["state"];
                Assert.True(state == "incomplete" || state == "complete",
                    "examples/" + Relative(path) + " drafts a context in state '" + state + "': " +
                    result.ToString(Formatting.None));
            }
        }

        [Fact]
        public void The_example_project_context_is_complete()
        {
            string path = Path.Combine(ExamplesRoot(), "iso19650-startup", "project-context.example.json");
            JObject evaluation = ProjectContext.Evaluate(Load(path));
            Assert.True((string)evaluation["state"] == "complete", evaluation.ToString(Formatting.None));
        }

        [Fact]
        public void The_example_container_names_compose_and_validate()
        {
            int ran = 0;
            foreach (var (path, doc) in Calls().Where(c => (string)c.Doc["tool"] == "horizun_information_container"))
            {
                var args = (JObject)doc["arguments"];
                if ((string)args["operation"] != "name") continue;
                JObject result = InformationContainerTool.Handle((JObject)args.DeepClone(), CancellationToken.None);
                Assert.True((bool)result["valid"], "examples/" + Relative(path) + ": " + result.ToString(Formatting.None));
                ran++;
            }
            Assert.True(ran > 0, "no container name example was run");

            // The other examples that carry a container must compose to a valid name too.
            foreach (var (path, doc) in Calls())
            {
                if (!(doc["arguments"]["information_container"] is JObject container)) continue;
                var nameArgs = new JObject { ["operation"] = "name", ["information_container"] = container.DeepClone() };
                JObject result = InformationContainerTool.Handle(nameArgs, CancellationToken.None);
                Assert.True((bool)result["valid"], "examples/" + Relative(path) + ": " + result.ToString(Formatting.None));
            }
        }

        // ---- the validator's own honesty -------------------------------------------------

        [Fact]
        public void The_validator_refuses_what_it_does_not_understand_and_catches_real_drift()
        {
            var errors = new List<string>();
            SchemaValidator.Validate(new JObject(), JObject.Parse("{\"type\":\"object\",\"dependentRequired\":{}}"), "", errors);
            Assert.Contains(errors, e => e.Contains("dependentRequired"));

            var schema = JObject.Parse(@"{""type"":""object"",""required"":[""a""],""additionalProperties"":false,
                ""properties"":{""a"":{""type"":""string"",""enum"":[""x"",""y""]},
                ""n"":{""type"":""integer"",""minimum"":1,""maximum"":3},
                ""k"":{""oneOf"":[{""properties"":{""kind"":{""const"":""p""}},""required"":[""kind""]},
                               {""properties"":{""kind"":{""const"":""q""}},""required"":[""kind""]}]}}}");
            errors.Clear(); SchemaValidator.Validate(JObject.Parse("{\"a\":\"x\",\"n\":2,\"k\":{\"kind\":\"p\"}}"), schema, "", errors);
            Assert.Empty(errors);
            errors.Clear(); SchemaValidator.Validate(JObject.Parse("{\"a\":\"z\"}"), schema, "", errors);
            Assert.NotEmpty(errors);
            errors.Clear(); SchemaValidator.Validate(JObject.Parse("{\"a\":\"x\",\"renamed\":1}"), schema, "", errors);
            Assert.Contains(errors, e => e.Contains("renamed"));
            errors.Clear(); SchemaValidator.Validate(JObject.Parse("{\"a\":\"x\",\"n\":9}"), schema, "", errors);
            Assert.NotEmpty(errors);
            errors.Clear(); SchemaValidator.Validate(JObject.Parse("{\"a\":\"x\",\"k\":{\"kind\":\"r\"}}"), schema, "", errors);
            Assert.NotEmpty(errors);
        }

        [Fact]
        public void The_validator_understands_every_keyword_the_contract_uses()
        {
            var failures = new List<string>();
            foreach (CommandContract c in Contract.All)
                foreach (string k in SchemaValidator.UnknownKeywords(c.InputSchema))
                    failures.Add(c.Name + ": " + k);
            Assert.True(failures.Count == 0,
                "The example validator does not understand these keywords; teach it before trusting it: " +
                string.Join(", ", failures));
        }

        /// <summary>
        /// A JSON Schema validator for exactly the keyword subset the contract's input schemas use.
        /// Unknown keywords are ERRORS, never skipped. `format` is an annotation here, as it is
        /// by default in JSON Schema 2020-12; `default` and `description` are annotations.
        /// </summary>
        internal static class SchemaValidator
        {
            private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.Ordinal)
            {
                "type", "enum", "const", "properties", "required", "additionalProperties", "items",
                "minItems", "maxItems", "uniqueItems", "minLength", "maxLength", "pattern",
                "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minProperties", "maxProperties",
                "allOf", "anyOf", "oneOf", "not", "if", "then", "else",
                "format", "default", "description", "title", "examples", "$comment"
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
                    else if (p.Name == "items" || p.Name == "additionalProperties" || p.Name == "not" ||
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

            internal static void Validate(JToken value, JToken schemaToken, string pointer, List<string> errors)
            {
                if (schemaToken is JValue b && b.Type == JTokenType.Boolean)
                {
                    if (!(bool)b) errors.Add(At(pointer) + "is not allowed here");
                    return;
                }
                if (!(schemaToken is JObject schema)) { errors.Add(At(pointer) + "schema is not an object"); return; }
                foreach (JProperty p in schema.Properties())
                    if (!Known.Contains(p.Name)) errors.Add(At(pointer) + "the validator does not understand keyword '" + p.Name + "'");

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
                        if (schema["pattern"] != null && !Regex.IsMatch(s, (string)schema["pattern"]))
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
                        if (schema["uniqueItems"] != null && (bool)schema["uniqueItems"])
                            for (int i = 0; i < arr.Count; i++)
                                for (int j = i + 1; j < arr.Count; j++)
                                    if (JToken.DeepEquals(arr[i], arr[j])) errors.Add(At(pointer) + "items " + i + " and " + j + " are equal");
                        if (schema["items"] != null)
                            for (int i = 0; i < arr.Count; i++) Validate(arr[i], schema["items"], pointer + "/" + i, errors);
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
                            if (props?[p.Name] != null) Validate(p.Value, props[p.Name], child, errors);
                            else if (schema["additionalProperties"] != null) Validate(p.Value, schema["additionalProperties"], child, errors);
                        }
                        break;
                }

                if (schema["allOf"] is JArray all)
                    foreach (JToken branch in all) Validate(value, branch, pointer, errors);
                if (schema["anyOf"] is JArray any && !any.Any(branch => Passes(value, branch, pointer)))
                    errors.Add(At(pointer) + "matches none of anyOf: " + Diagnose(value, any, pointer));
                if (schema["oneOf"] is JArray one)
                {
                    int matched = one.Count(branch => Passes(value, branch, pointer));
                    if (matched != 1)
                        errors.Add(At(pointer) + "matches " + matched + " branches of oneOf (exactly one required)" +
                                   (matched == 0 ? ": " + Diagnose(value, one, pointer) : ""));
                }
                if (schema["not"] != null && Passes(value, schema["not"], pointer))
                    errors.Add(At(pointer) + "matches a schema it must not match");
                if (schema["if"] != null)
                {
                    if (Passes(value, schema["if"], pointer)) { if (schema["then"] != null) Validate(value, schema["then"], pointer, errors); }
                    else if (schema["else"] != null) Validate(value, schema["else"], pointer, errors);
                }
            }

            private static bool Passes(JToken value, JToken schema, string pointer)
            {
                var errors = new List<string>();
                Validate(value, schema, pointer, errors);
                return errors.Count == 0;
            }

            /// <summary>The errors of the closest branch, so a failing oneOf says WHY.</summary>
            private static string Diagnose(JToken value, JArray branches, string pointer)
            {
                List<string> best = null;
                foreach (JToken branch in branches)
                {
                    var errors = new List<string>();
                    Validate(value, branch, pointer, errors);
                    if (best == null || errors.Count < best.Count) best = errors;
                }
                return best == null ? "" : string.Join("; ", best.Take(5));
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
}
