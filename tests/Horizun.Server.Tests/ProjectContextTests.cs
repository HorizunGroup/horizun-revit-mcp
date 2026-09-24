// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// horizun_project_context: the ISO 19650 project context and its intake.
//
// What must hold, and is proved here against the shipped handler and the schema
// embedded exactly as the server embeds it:
//
//   * the schema is the repository file, byte for byte, and uses no keyword the
//     small validator does not implement - otherwise "valid" would mean "valid
//     for the keywords somebody remembered";
//   * INVALID, INCONSISTENT and INCOMPLETE are three different answers;
//   * the questions come in the intake order, and every one of them points at a
//     place the schema defines, with the schema's own options;
//   * draft rehearses by default, writes only when asked, re-reads what it wrote,
//     never replaces a file without overwrite=true, and never writes an invalid
//     context or a credential.
//
// Real disposable files under a settings root of their own - never the machine's.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class ProjectContextTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _settingsRoot;
        private readonly string _savedRoot;

        public ProjectContextTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-pc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _settingsRoot = Path.Combine(Path.GetTempPath(), "hz-pc-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_settingsRoot);
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _settingsRoot);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _savedRoot);
            try { Directory.Delete(_dir, true); } catch { }
            try { Directory.Delete(_settingsRoot, true); } catch { }
        }

        private void Profile(string profile)
            => File.WriteAllText(HorizunPaths.SettingsPath(), @"{""permission_profile"":""" + profile + @"""}");

        private string Write(string name, JObject doc)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, doc.ToString());
            return path;
        }

        private static JObject Call(JObject args) => ProjectContext.Handle(args);

        // ---- fixtures -------------------------------------------------------------

        /// <summary>A context that answers every intake question, coherently. Neutral data.</summary>
        private static JObject Complete() => JObject.Parse(@"{
  ""schema_version"": 1,
  ""project"": { ""code"": ""HZ01"", ""name"": ""Test building"" },
  ""appointment"": {
    ""role"": ""lead_appointed_party"", ""organisation_code"": ""ORG"",
    ""task_teams"": [ { ""role_code"": ""A"", ""discipline"": ""Architecture"" }, { ""role_code"": ""S"", ""discipline"": ""Structure"" } ],
    ""stage"": ""Design development"", ""stage_scheme"": ""iso22263""
  },
  ""documents"": {
    ""eir"": { ""status"": ""approved"", ""path"": ""docs/eir.pdf"" },
    ""bep"": { ""kind"": ""post_appointment"", ""status"": ""draft"", ""path"": ""docs/bep.xlsx"" },
    ""midp"": { ""status"": ""draft"", ""path"": ""docs/midp.xlsx"" },
    ""tidp"": [ { ""task_team"": ""A"", ""path"": ""docs/tidp-a.xlsx"" } ],
    ""responsibility_matrix"": { ""status"": ""missing"" },
    ""loin"": { ""status"": ""received"", ""path"": ""docs/loin.xlsx"" },
    ""ids"": [ { ""path"": ""docs/arch.ids"", ""applies_to"": ""architecture IFC"" } ]
  },
  ""deliverables"": [
    { ""container"": ""HZ01-ORG-ZZ-XX-M3-A-0001"", ""task_team"": ""A"", ""required_status"": ""S2"", ""format"": ""ifc"", ""due"": ""2026-10-01"" }
  ],
  ""cde"": {
    ""platform"": ""sharepoint"", ""project_ref"": ""project-42"", ""root"": ""D:/sync/HZ01"",
    ""states"": { ""wip"": ""01_WIP"", ""shared"": ""02_SHARED"", ""published"": ""03_PUBLISHED"", ""archived"": ""04_ARCHIVED"" },
    ""working_state"": ""wip"",
    ""approvals"": { ""wip_to_shared"": ""Task team lead"", ""shared_to_published"": ""Information manager"", ""published_to_archived"": ""Information manager"" }
  },
  ""naming"": {
    ""scheme"": ""iso19650-2"", ""separator"": ""-"",
    ""fields"": [
      { ""name"": ""project"", ""pattern"": ""^[A-Z0-9]{2,6}$"" }, { ""name"": ""originator"", ""pattern"": ""^[A-Z0-9]{3,6}$"" },
      { ""name"": ""volume"", ""pattern"": ""^[A-Z0-9]{2}$"" }, { ""name"": ""level"", ""pattern"": ""^[A-Z0-9]{2}$"" },
      { ""name"": ""type"", ""pattern"": ""^[A-Z0-9]{2}$"" }, { ""name"": ""role"", ""pattern"": ""^[A-Z0-9]{1,2}$"" },
      { ""name"": ""number"", ""pattern"": ""^[0-9]{4,6}$"" }
    ],
    ""status_codes"": { ""S0"": ""WIP"", ""S2"": ""Information"", ""A1"": ""Accepted"" },
    ""revision"": { ""preliminary_pattern"": ""^P[0-9]{2}$"", ""contractual_pattern"": ""^C[0-9]{2}$"" }
  },
  ""classification"": { ""system"": ""uniclass2015"", ""catalog_path"": ""docs/uniclass.csv"", ""type_parameter"": ""Keynote"" },
  ""georeference"": { ""crs"": ""EPSG:4326"", ""survey_point"": { ""n"": 0, ""e"": 0, ""z"": 0 } },
  ""delivery"": { ""ifc"": { ""version"": ""IFC4"", ""mvd"": ""RV"", ""pset_mapping_path"": ""docs/psets.txt"", ""site_placement"": ""shared"" } },
  ""software"": { ""revit_year"": 2025 },
  ""intake"": { ""missing"": [] }
}");

        // ---- the schema -----------------------------------------------------------

        [Fact]
        public void The_embedded_schema_is_the_repository_file_and_declares_its_id()
        {
            string repoFile = Path.Combine(RepositoryRoot(), "schemas", "project-context.v1.schema.json");
            Assert.Equal(File.ReadAllText(repoFile), ProjectContext.SchemaText);

            JObject schema = ProjectContext.Schema;
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", (string)schema["$schema"]);
            Assert.Equal("https://horizunhub.com/schemas/project-context/v1", (string)schema["$id"]);
            Assert.Equal(new[] { "schema_version", "project" }, schema["required"].Select(t => (string)t).ToArray());
            Assert.Equal(1, (int)schema["properties"]["schema_version"]["const"]);
            Assert.Equal(new[] { "code" }, schema["properties"]["project"]["required"].Select(t => (string)t).ToArray());

            JObject reply = Call(new JObject { ["operation"] = "schema" });
            Assert.True(JToken.DeepEquals(schema, reply["schema"]));
            Assert.Equal(ProjectContext.SchemaResourceUri, (string)reply["resource_uri"]);
            Assert.Equal(64, ((string)reply["sha256"]).Length);
        }

        [Fact]
        public void The_schema_uses_only_keywords_the_validator_implements()
        {
            var unknown = new List<string>();
            WalkSchema(ProjectContext.Schema, "#", unknown);
            Assert.True(unknown.Count == 0,
                "The schema uses keywords ProjectContext.ValidateAgainst does not implement, so 'valid' would not " +
                "mean valid: " + string.Join(", ", unknown));
        }

        private static void WalkSchema(JObject node, string at, List<string> unknown)
        {
            foreach (JProperty p in node.Properties())
            {
                if (!ProjectContext.SupportedKeywords.Contains(p.Name)) unknown.Add(at + "/" + p.Name);
                if ((p.Name == "properties" || p.Name == "$defs") && p.Value is JObject children)
                    foreach (JProperty child in children.Properties())
                        WalkSchema((JObject)child.Value, at + "/" + p.Name + "/" + child.Name, unknown);
                if ((p.Name == "items" || p.Name == "additionalProperties") && p.Value is JObject sub)
                    WalkSchema(sub, at + "/" + p.Name, unknown);
            }
        }

        [Fact]
        public void The_schema_is_served_as_an_mcp_resource()
        {
            JArray resources = (JArray)McpResources.List(null)["resources"];
            JObject row = resources.OfType<JObject>().Single(r => (string)r["uri"] == "horizun://schemas/project-context/v1");
            Assert.Equal("application/schema+json", (string)row["mimeType"]);

            JObject read = McpResources.Read(new JObject { ["uri"] = "horizun://schemas/project-context/v1" });
            Assert.Equal(ProjectContext.SchemaText, (string)read["contents"][0]["text"]);
        }

        // ---- validate: invalid / inconsistent / incomplete / complete ------------------

        [Fact]
        public void A_complete_coherent_context_is_complete()
        {
            JObject reply = Call(new JObject { ["operation"] = "validate", ["path"] = Write("ok.json", Complete()) });
            Assert.True((bool)reply["valid"], reply.ToString());
            Assert.True((bool)reply["coherent"], reply["coherence"].ToString());
            Assert.Empty((JArray)reply["missing"]);
            Assert.Equal("complete", (string)reply["state"]);
            Assert.Equal(new[] { "responsibility_matrix" }, reply["documents_declared_missing"].Select(t => (string)t));
        }

        [Fact]
        public void A_minimal_context_is_valid_but_incomplete_and_lists_what_is_missing_in_order()
        {
            var doc = new JObject { ["schema_version"] = 1, ["project"] = new JObject { ["code"] = "HZ01" } };
            JObject reply = Call(new JObject { ["operation"] = "validate", ["path"] = Write("min.json", doc) });
            Assert.True((bool)reply["valid"]);
            Assert.Equal("incomplete", (string)reply["state"]);
            var ids = reply["missing"].Select(m => (string)m["id"]).ToList();
            Assert.Equal("project_name", ids[0]);
            Assert.Equal("appointment_role", ids[1]);
            Assert.Contains("eir_status", ids);
            Assert.Contains("bep_kind", ids);
            Assert.Contains("cde_state_published", ids);
            Assert.Contains("cde_approval_shared_to_published", ids);
            Assert.Equal("revit_year", ids.Last());
            Assert.All(reply["missing"], m => Assert.StartsWith("/", (string)m["pointer"]));
        }

        [Fact]
        public void Schema_violations_are_invalid_with_json_pointers_and_are_not_mistaken_for_gaps()
        {
            JObject doc = Complete();
            doc["schema_version"] = 2;
            doc["appointment"]["role"] = "owner";
            doc["deliverables"][0]["due"] = "01/10/2026";
            doc["naming"]["fields"][0]["pattern"] = "^[A-Z";
            doc["software"]["revit_year"] = "2025";
            doc["cde"]["colour"] = "blue";
            ((JObject)doc["project"]).Remove("code");

            JObject reply = Call(new JObject { ["operation"] = "validate", ["path"] = Write("bad.json", doc) });
            Assert.False((bool)reply["valid"]);
            Assert.Equal("invalid", (string)reply["state"]);
            var pointers = reply["errors"].Select(e => (string)e["pointer"] + " " + (string)e["keyword"]).ToList();
            Assert.Contains("/schema_version const", pointers);
            Assert.Contains("/appointment/role enum", pointers);
            Assert.Contains("/deliverables/0/due pattern", pointers);
            Assert.Contains("/naming/fields/0/pattern format", pointers);
            Assert.Contains("/software/revit_year type", pointers);
            Assert.Contains("/cde/colour additionalProperties", pointers);
            Assert.Contains("/project/code required", pointers);
        }

        [Fact]
        public void A_file_that_is_not_json_is_invalid_not_an_exception()
        {
            string path = Path.Combine(_dir, "broken.json");
            File.WriteAllText(path, "{ \"schema_version\": 1, ");
            JObject reply = Call(new JObject { ["operation"] = "validate", ["path"] = path });
            Assert.Equal("invalid", (string)reply["state"]);
            Assert.Equal("json", (string)reply["errors"][0]["keyword"]);
        }

        [Fact]
        public void Contradictions_are_inconsistent_and_named_by_rule()
        {
            JObject doc = Complete();
            doc["deliverables"] = JArray.Parse(@"[
                { ""container"": ""HZ01-ORG-ZZ-XX-M3-A"" },
                { ""container"": ""HZ01-ORG-ZZ-XX-M3-A-12"" },
                { ""container"": ""XX99-ORG-ZZ-XX-M3-A-0001"", ""required_status"": ""S9"", ""task_team"": ""Q"" }
            ]");
            ((JObject)doc["cde"]["states"]).Remove("published");
            ((JObject)doc["documents"]["bep"]).Remove("kind");
            doc["cde"]["project_ref"] = "https://cde.example/p/42?token=abc123";

            JObject reply = Call(new JObject { ["operation"] = "validate", ["path"] = Write("contra.json", doc) });
            Assert.True((bool)reply["valid"], reply["errors"].ToString());
            Assert.Equal("inconsistent", (string)reply["state"]);
            var rules = reply["coherence"].Select(f => (string)f["rule"] + "@" + (string)f["pointer"]).ToList();
            Assert.Contains("container_field_count@/deliverables/0/container", rules);
            Assert.Contains("container_field_mismatch@/deliverables/1/container", rules);
            Assert.Contains("container_project_mismatch@/deliverables/2/container", rules);
            Assert.Contains("unknown_status_code@/deliverables/2/required_status", rules);
            Assert.Contains("unknown_task_team@/deliverables/2/task_team", rules);
            Assert.Contains("cde_state_without_folder@/cde/states/published", rules);
            Assert.Contains("bep_kind_absent@/documents/bep/kind", rules);
            Assert.Contains("credential_like@/cde/project_ref", rules);
            // The gaps the contradictions open are ALSO reported as missing, separately.
            Assert.Contains(reply["missing"], m => (string)m["id"] == "cde_state_published");
            Assert.Contains(reply["missing"], m => (string)m["id"] == "bep_kind");
        }

        // ---- questions --------------------------------------------------------------

        [Fact]
        public void The_questions_follow_the_intake_order()
        {
            JObject reply = Call(new JObject { ["operation"] = "questions" });
            var sections = new List<string>();
            foreach (JToken q in reply["questions"])
                if (sections.Count == 0 || sections.Last() != (string)q["section"]) sections.Add((string)q["section"]);
            Assert.Equal(new[]
            {
                "project", "appointment", "stage", "eir", "bep", "midp_tidp", "responsibility_matrix", "cde",
                "naming", "classification", "loin_ids", "georeference", "ifc", "software"
            }, sections);
            // 'order' is the position in the whole intake, so it only ever increases; a
            // question that does not apply leaves its number unused rather than renumbering.
            var orders = reply["questions"].Select(q => (int)q["order"]).ToList();
            Assert.Equal(orders.OrderBy(o => o), orders);
            Assert.Equal(orders.Count, orders.Distinct().Count());
            Assert.Equal("project_code", (string)reply["next_question_id"]);

            // Inside the CDE block the order is the one the intake asks in.
            var cde = reply["questions"].Where(q => (string)q["section"] == "cde").Select(q => (string)q["id"]).ToList();
            Assert.Equal(new[]
            {
                "cde_platform", "cde_project_ref", "cde_root", "cde_state_wip", "cde_state_shared", "cde_state_published",
                "cde_state_archived", "cde_working_state", "cde_approval_wip_to_shared",
                "cde_approval_shared_to_published", "cde_approval_published_to_archived"
            }, cde);
        }

        [Fact]
        public void Every_question_is_bilingual_explains_itself_and_targets_a_place_the_schema_defines()
        {
            JObject schema = ProjectContext.Schema;
            foreach (JObject q in Call(new JObject { ["operation"] = "questions" })["questions"].OfType<JObject>())
            {
                string id = (string)q["id"];
                foreach (string lang in new[] { "es", "en" })
                {
                    Assert.False(string.IsNullOrWhiteSpace((string)q["text"][lang]), id + " text." + lang);
                    Assert.False(string.IsNullOrWhiteSpace((string)q["why"][lang]), id + " why." + lang);
                }
                JObject target = SchemaAt(schema, (string)q["pointer"]);
                Assert.True(target != null, id + " targets " + (string)q["pointer"] + ", which the schema does not define");

                // An enum question offers exactly the schema's values - no more, no fewer.
                if ((string)q["type"] == "enum")
                {
                    JArray allowed = (JArray)(target["enum"] ?? Deref(schema, target)?["enum"]);
                    Assert.NotNull(allowed);
                    Assert.Equal(allowed.Select(v => (string)v).OrderBy(v => v),
                                 q["options"].Select(o => (string)o["value"]).OrderBy(v => v));
                    Assert.False((bool)q["allow_other"], id);
                }
            }
        }

        private static JObject Deref(JObject schema, JObject node)
            => node?["$ref"] is JValue r ? (JObject)ProjectContext.Resolve(schema, ((string)r).Substring(1)) : node;

        /// <summary>The subschema a JSON pointer into a DOCUMENT lands on, following $ref and items.</summary>
        private static JObject SchemaAt(JObject schema, string pointer)
        {
            JObject node = schema;
            foreach (string seg in pointer.Substring(1).Split('/'))
            {
                node = Deref(schema, node);
                if (node == null) return null;
                if (node["properties"]?[seg] is JObject child) node = child;
                else if ((string)node["type"] == "array" && int.TryParse(seg, out _)) node = node["items"] as JObject;
                else if (node["additionalProperties"] is JObject ap) node = ap;
                else return null;
            }
            return Deref(schema, node);
        }

        [Fact]
        public void Questions_against_a_file_skip_what_is_answered_and_what_does_not_apply()
        {
            JObject doc = JObject.Parse(@"{ ""schema_version"": 1, ""project"": { ""code"": ""HZ01"", ""name"": ""X"" },
                ""appointment"": { ""role"": ""appointed_party"" },
                ""documents"": { ""eir"": { ""status"": ""missing"" } },
                ""classification"": { ""system"": ""uniclass2015"" } }");
            JObject reply = Call(new JObject { ["operation"] = "questions", ["path"] = Write("partial.json", doc) });
            var ids = reply["questions"].Select(q => (string)q["id"]).ToList();
            Assert.DoesNotContain("project_code", ids);
            Assert.DoesNotContain("appointment_role", ids);
            Assert.DoesNotContain("eir_status", ids);
            Assert.DoesNotContain("eir_path", ids);             // no EIR exists: asking where it is makes no sense
            Assert.DoesNotContain("classification_name", ids);  // only for a custom system
            Assert.Equal("organisation_code", (string)reply["next_question_id"]);
            Assert.True((int)reply["not_applicable"] >= 2);

            JObject all = Call(new JObject { ["operation"] = "questions", ["path"] = Path.Combine(_dir, "partial.json"), ["include_answered"] = true });
            JObject role = all["questions"].OfType<JObject>().Single(q => (string)q["id"] == "appointment_role");
            Assert.Equal("answered", (string)role["state"]);
            Assert.Equal("appointed_party", (string)role["current_value"]);
            Assert.Equal("not_applicable", (string)all["questions"].Single(q => (string)q["id"] == "eir_path")["state"]);
        }

        // ---- draft ------------------------------------------------------------------

        private static JObject Answers() => new JObject
        {
            ["/project/code"] = "HZ01",
            ["/appointment/role"] = "appointed_party",
            ["/cde/states/wip"] = "01_WIP",
            ["/documents/tidp/0/task_team"] = "A"
        };

        [Fact]
        public void Draft_rehearses_by_default_and_writes_nothing()
        {
            string path = Path.Combine(_dir, "ctx.json");
            JObject reply = Call(new JObject { ["operation"] = "draft", ["answers"] = Answers(), ["path"] = path });
            Assert.True((bool)reply["dry_run"]);
            Assert.False((bool)reply["written"]);
            Assert.False(File.Exists(path));
            Assert.Equal(1, (int)reply["document"]["schema_version"]);
            Assert.Equal("01_WIP", (string)reply["document"]["cde"]["states"]["wip"]);
            Assert.Equal("A", (string)reply["document"]["documents"]["tidp"][0]["task_team"]);
            // Unknowns are listed, never invented.
            var missing = reply["document"]["intake"]["missing"].Select(t => (string)t).ToList();
            Assert.Contains("eir", missing);
            Assert.Contains("midp", missing);
            Assert.Null(reply["document"]["documents"]["eir"]);
            Assert.Equal("incomplete", (string)reply["state"]);
        }

        [Fact]
        public void Draft_writes_only_under_full_write_and_reads_back_what_it_wrote()
        {
            string path = Path.Combine(_dir, "ctx.json");
            JObject write = new JObject { ["operation"] = "draft", ["answers"] = Answers(), ["path"] = path, ["dry_run"] = false };

            Profile("safe_write");
            ToolRefusal refused = Assert.Throws<ToolRefusal>(() => Call(write));
            Assert.Contains("permission_profile=safe_write", refused.Message);
            Assert.False(File.Exists(path));

            Profile("full_write");
            JObject reply = Call((JObject)write.DeepClone());
            Assert.True((bool)reply["written"]);
            Assert.True((bool)reply["verification"]["reread"]);
            Assert.Equal(ProjectContext.Sha256Hex(File.ReadAllBytes(path)), (string)reply["verification"]["sha256"]);
            Assert.True(JToken.DeepEquals(JObject.Parse(File.ReadAllText(path)), reply["document"]));

            JObject validated = Call(new JObject { ["operation"] = "validate", ["path"] = path });
            Assert.True((bool)validated["valid"]);
        }

        [Fact]
        public void Draft_never_replaces_an_existing_file_without_overwrite_and_builds_on_it()
        {
            Profile("full_write");
            string path = Write("existing.json", JObject.Parse(@"{ ""schema_version"": 1, ""project"": { ""code"": ""HZ01"", ""name"": ""Kept"" } }"));
            byte[] before = File.ReadAllBytes(path);
            var answers = new JObject { ["/appointment/stage"] = "Concept" };

            JObject rehearsal = Call(new JObject { ["operation"] = "draft", ["answers"] = answers, ["path"] = path });
            Assert.Equal("existing_file", (string)rehearsal["base"]);
            Assert.Contains("overwrite", (string)rehearsal["would_refuse"]);

            ToolRefusal refused = Assert.Throws<ToolRefusal>(() => Call(new JObject
            {
                ["operation"] = "draft", ["answers"] = answers.DeepClone(), ["path"] = path, ["dry_run"] = false
            }));
            Assert.Contains("overwrite", refused.Message);
            Assert.Equal(before, File.ReadAllBytes(path));

            JObject written = Call(new JObject
            {
                ["operation"] = "draft", ["answers"] = answers.DeepClone(), ["path"] = path, ["dry_run"] = false, ["overwrite"] = true
            });
            Assert.True((bool)written["written"]);
            JObject onDisk = JObject.Parse(File.ReadAllText(path));
            Assert.Equal("Kept", (string)onDisk["project"]["name"]);     // the first round survives
            Assert.Equal("Concept", (string)onDisk["appointment"]["stage"]);
        }

        [Fact]
        public void Draft_refuses_to_write_an_invalid_context_or_a_credential_and_rejects_bad_pointers()
        {
            Profile("full_write");
            string path = Path.Combine(_dir, "refused.json");

            var invalid = Answers();
            invalid["/appointment/role"] = "owner";
            JObject rehearsal = Call(new JObject { ["operation"] = "draft", ["answers"] = invalid, ["path"] = path });
            Assert.Equal("invalid", (string)rehearsal["state"]);
            Assert.Throws<ToolRefusal>(() => Call(new JObject
            {
                ["operation"] = "draft", ["answers"] = invalid.DeepClone(), ["path"] = path, ["dry_run"] = false
            }));
            Assert.False(File.Exists(path));

            var secret = Answers();
            secret["/cde/project_ref"] = "https://user:hunter2@cdehost/p/42";
            ToolRefusal credential = Assert.Throws<ToolRefusal>(() => Call(new JObject
            {
                ["operation"] = "draft", ["answers"] = secret, ["path"] = path, ["dry_run"] = false
            }));
            Assert.Contains("credential", credential.Message);
            Assert.False(File.Exists(path));

            Assert.Throws<ToolRefusal>(() => Call(new JObject
            {
                ["operation"] = "draft", ["answers"] = new JObject { ["project/code"] = "HZ01" }
            }));
            Assert.Throws<ToolRefusal>(() => Call(new JObject
            {
                ["operation"] = "draft", ["answers"] = new JObject { ["/project/code"] = "HZ01", ["/project/code/x"] = "HZ01" }
            }));
        }

        [Fact]
        public void Relative_paths_and_unknown_operations_are_refused()
        {
            Assert.Throws<ToolRefusal>(() => Call(new JObject { ["operation"] = "validate", ["path"] = "ctx.json" }));
            Assert.Throws<ToolRefusal>(() => Call(new JObject { ["operation"] = "rewrite" }));
            Assert.Throws<ToolRefusal>(() => Call(new JObject()));
        }

        // ---- the contract -------------------------------------------------------------

        [Fact]
        public void The_contract_declares_a_host_resident_tool_that_writes_only_on_request()
        {
            CommandContract c = Contract.Find("horizun_project_context");
            Assert.NotNull(c);
            Assert.Null(c.Command);
            Assert.Equal(ToolEffect.ExternalSideEffectOnRequest, c.Effect);
            Assert.False(c.Destructive);
            Assert.True(c.OpenWorld);
            Assert.Null(c.InputSchema["properties"]["idempotency_key"]);
            Assert.Equal(new[] { "schema", "validate", "questions", "draft", "ids_from_loin" },
                         c.InputSchema["properties"]["operation"]["enum"].Select(t => (string)t));
            Assert.NotNull(Tools.Find("horizun_project_context").Host);
        }

        [Fact]
        public void The_intake_prompt_drives_questions_then_a_rehearsed_draft()
        {
            JObject listed = ((JArray)McpPrompts.List(null)["prompts"]).OfType<JObject>()
                .Single(p => (string)p["name"] == "project-intake");
            Assert.Equal("Start a BIM project the ISO 19650 way", (string)listed["title"]);
            Assert.False((bool)listed["arguments"][0]["required"]);

            string text = (string)McpPrompts.Get(new JObject
            {
                ["name"] = "project-intake",
                ["arguments"] = new JObject { ["path"] = "D:/projects/p01/project-context.json" }
            })["messages"][0]["content"]["text"];
            Assert.Contains("operation=questions", text);
            Assert.Contains("dry_run=true", text);
            Assert.Contains("NEVER answer", text);
            Assert.Contains("D:/projects/p01/project-context.json", text);
            Assert.DoesNotContain("cache_mode", text);

            string noPath = (string)McpPrompts.Get(new JObject { ["name"] = "project-intake" })["messages"][0]["content"]["text"];
            Assert.Contains("agree one with the person", noPath);
        }

        private static string RepositoryRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "schemas"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }
    }
}
