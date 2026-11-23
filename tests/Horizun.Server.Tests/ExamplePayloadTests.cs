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
            "disciplines/architecture-walls-and-slab.json",
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

        // A Power BI project (.pbip) keeps its report and model as Microsoft's own JSON
        // (page.json, visual.json, themes) under <name>.Report / <name>.SemanticModel.
        // Those are not payloads of this server and are validated by Power BI's schema,
        // so they are skipped - and only they: any other folder is still checked.
        private static bool InPowerBiProject(string path) =>
            Relative(path).Split('/').Any(part =>
                part.EndsWith(".Report", StringComparison.Ordinal) ||
                part.EndsWith(".SemanticModel", StringComparison.Ordinal));

        private static List<string> JsonFiles() =>
            Directory.GetFiles(ExamplesRoot(), "*.json", SearchOption.AllDirectories)
                     .Where(p => !InPowerBiProject(p))
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
                ContractSchemaCheck.Validate(doc["arguments"], contract.InputSchema, "", errors);
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
            ContractSchemaCheck.Validate(new JObject(), JObject.Parse("{\"type\":\"object\",\"dependentRequired\":{}}"), "", errors);
            Assert.Contains(errors, e => e.Contains("dependentRequired"));

            var schema = JObject.Parse(@"{""type"":""object"",""required"":[""a""],""additionalProperties"":false,
                ""properties"":{""a"":{""type"":""string"",""enum"":[""x"",""y""]},
                ""n"":{""type"":""integer"",""minimum"":1,""maximum"":3},
                ""k"":{""oneOf"":[{""properties"":{""kind"":{""const"":""p""}},""required"":[""kind""]},
                               {""properties"":{""kind"":{""const"":""q""}},""required"":[""kind""]}]}}}");
            errors.Clear(); ContractSchemaCheck.Validate(JObject.Parse("{\"a\":\"x\",\"n\":2,\"k\":{\"kind\":\"p\"}}"), schema, "", errors);
            Assert.Empty(errors);
            errors.Clear(); ContractSchemaCheck.Validate(JObject.Parse("{\"a\":\"z\"}"), schema, "", errors);
            Assert.NotEmpty(errors);
            errors.Clear(); ContractSchemaCheck.Validate(JObject.Parse("{\"a\":\"x\",\"renamed\":1}"), schema, "", errors);
            Assert.Contains(errors, e => e.Contains("renamed"));
            errors.Clear(); ContractSchemaCheck.Validate(JObject.Parse("{\"a\":\"x\",\"n\":9}"), schema, "", errors);
            Assert.NotEmpty(errors);
            errors.Clear(); ContractSchemaCheck.Validate(JObject.Parse("{\"a\":\"x\",\"k\":{\"kind\":\"r\"}}"), schema, "", errors);
            Assert.NotEmpty(errors);
        }

        [Fact]
        public void The_validator_understands_every_keyword_the_contract_uses()
        {
            var failures = new List<string>();
            foreach (CommandContract c in Contract.All)
                foreach (string k in ContractSchemaCheck.UnknownKeywords(c.InputSchema))
                    failures.Add(c.Name + ": " + k);
            Assert.True(failures.Count == 0,
                "The example validator does not understand these keywords; teach it before trusting it: " +
                string.Join(", ", failures));
        }
    }
}
