// schema_help is advice attached AFTER the verdict. These facts use the real reply
// builders (McpResult.Structured / Error / Text) and the create_elements rehearsal
// fields (valid, invalid, errors, note), and prove two things: a failed call that
// violates the contract gets the exact branch it violated, and nothing a client
// already parses - isError, the success text, invalid, errors, fallback,
// capability_gaps - changes by a single byte.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Horizun.Contracts;
using Horizun.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class SchemaHelpTests
    {
        private const string CreateElements = "horizun_create_elements";

        private static VariantSite Kinds => ContractVariants.For(CreateElements).Single();

        private static JObject Rehearsal(int valid, int invalid)
        {
            var data = new JObject
            {
                ["valid"] = valid,
                ["invalid"] = invalid,
                ["errors"] = new JArray(Enumerable.Range(0, invalid).Select(i =>
                    new JObject { ["index"] = i, ["error"] = "row " + i + " is not buildable" })),
                ["note"] = "dry_run: nothing was written."
            };
            return McpResult.Structured(data, data.ToString(Formatting.None));
        }

        private static JObject Rows(params JObject[] rows) => new JObject { ["elements"] = new JArray(rows) };

        private static JObject Help(JToken attached) => (JObject)attached["structuredContent"]?["schema_help"];

        [Fact]
        public void A_wall_row_missing_its_fields_gets_the_wall_branch_on_an_invalid_rehearsal()
        {
            JObject reply = Rehearsal(0, 1);
            JObject args = Rows(new JObject { ["kind"] = "wall" });
            JToken attached = SchemaHelp.Attach(reply, CreateElements, args);
            JObject help = Help(attached);

            Assert.NotNull(help);
            Assert.Equal("horizun://contract/tools/" + CreateElements, (string)help["contract_uri"]);
            Assert.Contains(help["violations"].Select(v => (string)v), v => v.StartsWith("/elements/0", StringComparison.Ordinal));
            JObject wall = (JObject)help["variants"][0];
            Assert.Equal("wall", (string)wall["value"]);
            Assert.Equal("kind", (string)wall["discriminator"]);
            Assert.Equal("/elements/0", (string)wall["pointer"]);
            Assert.Equal("horizun://contract/tools/" + CreateElements + "/wall", (string)wall["uri"]);
            Assert.True(JToken.DeepEquals(Kinds.Branch("wall"), wall["schema"]));

            // The violation text agrees with the variant it names: a wall row is told what
            // the WALL branch needs, never to become another kind (review 2026-09-26: the
            // fewest-errors branch was level, and the advice said kind must be "level").
            List<string> said = help["violations"].Select(v => (string)v).ToList();
            Assert.Contains(said, v => v.Contains("'level_id' is required"));
            foreach (string other in Kinds.Values.Where(k => k != "wall"))
                Assert.DoesNotContain(said, v => v.Contains("must be \"" + other + "\""));

            // A success keeps its text exactly the payload, and its verdict fields.
            Assert.Equal((string)reply["content"][0]["text"], (string)attached["content"][0]["text"]);
            Assert.False((bool)attached["isError"]);
            foreach (string k in new[] { "valid", "invalid", "errors", "note" })
                Assert.True(JToken.DeepEquals(reply["structuredContent"][k], attached["structuredContent"][k]), k);
            // ...and the caller's object is not written to.
            Assert.Null(reply["structuredContent"]["schema_help"]);
        }

        [Fact]
        public void Schema_rejection_carries_a_contract_valid_minimal_call_and_safe_failure_path()
        {
            JObject args = new JObject { ["path"] = "private-model-path.rvt" };
            JObject reply = McpResult.Error("open refused.", null, null, new JObject());
            JObject help = Help(SchemaHelp.Attach(reply, "horizun_open_document", args));
            JObject example = (JObject)help["minimal_valid_call"];
            Assert.Equal("horizun_open_document", (string)example["tool"]);
            Assert.Empty(ContractSchemaCheck.Validate(example["arguments"], Contract.Find("horizun_open_document").InputSchema));
            Assert.Equal("/", (string)help["failure_path"]);
            Assert.Equal("required", (string)help["failure_rule"]);
            Assert.Equal("/", (string)help["schema_rejection"]["path"]);
            Assert.Equal("required", (string)help["schema_rejection"]["rule"]);
            Assert.DoesNotContain("private-model-path", help.ToString(Formatting.None));
        }

        [Fact]
        public void Nested_unknown_fields_report_the_exact_schema_path_and_rule()
        {
            JObject args = JObject.Parse("{elements:[{kind:'wall',unused_field:'sensitive-value'}]}");
            JObject attached = (JObject)SchemaHelp.Attach(McpResult.Error("invalid row", null, null, new JObject()),
                "horizun_create_elements", args);
            JObject rejection = (JObject)attached["structuredContent"]["schema_help"]["schema_rejection"];
            Assert.Equal("/elements/0/unused_field", (string)rejection["path"]);
            Assert.Equal("additional_property", (string)rejection["rule"]);
            Assert.DoesNotContain("sensitive-value", rejection.ToString(Formatting.None));
        }

        [Theory]
        [InlineData("horizun_execute_python")]
        [InlineData("horizun_query_model")]
        [InlineData("horizun_list_elements")]
        [InlineData("horizun_open_document")]
        [InlineData("horizun_document_session")]
        [InlineData("horizun_manage_views")]
        [InlineData("horizun_create_elements")]
        [InlineData("horizun_save_document")]
        public void Commonly_misassembled_tools_have_a_valid_minimal_contract_example(string tool)
        {
            JObject example = SchemaHelp.ContractExample(tool);
            Assert.NotNull(example);
            Assert.Empty(ContractSchemaCheck.Validate(example, Contract.Find(tool).InputSchema));
        }

        [Fact]
        public void Nothing_is_attached_when_the_contract_finds_nothing_or_nothing_failed()
        {
            JObject failed = Rehearsal(0, 1);
            Assert.Same(failed, SchemaHelp.Attach(failed, CreateElements, Rows(new JObject { ["kind"] = "wall" }),
                (a, s) => new List<string>()));

            JObject clean = Rehearsal(2, 0);
            Assert.Same(clean, SchemaHelp.Attach(clean, CreateElements, Rows(new JObject { ["kind"] = "wall" })));

            JObject text = McpResult.Text("{\"ok\":true}", false);
            Assert.Same(text, SchemaHelp.Attach(text, CreateElements, Rows(new JObject { ["kind"] = "wall" })));

            JObject task = new JObject { ["task"] = new JObject { ["taskId"] = "t1" }, ["content"] = new JArray() };
            Assert.Same(task, SchemaHelp.Attach(task, CreateElements, Rows(new JObject { ["kind"] = "wall" })));

            JObject unknownTool = McpResult.Text("refused", true);
            Assert.Same(unknownTool, SchemaHelp.Attach(unknownTool, "horizun_no_such_tool", new JObject { ["x"] = 1 }));
        }

        [Fact]
        public void An_error_keeps_every_verdict_byte_and_gains_one_line()
        {
            var fallback = new JObject
            {
                ["recommended_tool"] = "horizun_execute_python", ["allowed"] = false,
                ["reason"] = "invalid_arguments", ["write_started"] = false
            };
            var gaps = new JArray(new JObject { ["index"] = 1, ["kind"] = "no_such_kind" });
            JObject reply = McpResult.Error("create_elements refused: row 0 is missing fields.", fallback, gaps);
            string before = reply.ToString(Formatting.None);

            JToken attached = SchemaHelp.Attach(reply, CreateElements,
                Rows(new JObject { ["kind"] = "wall" }, new JObject { ["kind"] = "no_such_kind" }));

            Assert.Equal(before, reply.ToString(Formatting.None));
            Assert.True((bool)attached["isError"]);
            Assert.True(JToken.DeepEquals(reply["structuredContent"]["fallback"], attached["structuredContent"]["fallback"]));
            Assert.True(JToken.DeepEquals(reply["structuredContent"]["capability_gaps"], attached["structuredContent"]["capability_gaps"]));

            string original = (string)reply["content"][0]["text"];
            string now = (string)attached["content"][0]["text"];
            Assert.StartsWith(original + Environment.NewLine + Environment.NewLine, now);
            string line = now.Substring(original.Length + 2 * Environment.NewLine.Length);
            Assert.DoesNotContain("\n", line);
            Assert.StartsWith("Arguments violate the contract at /", line);
            Assert.Contains("structuredContent.schema_help", line);
            Assert.Contains("horizun://contract/tools/" + CreateElements, line);

            JObject unknown = Help(attached)["variants"].OfType<JObject>().Single(v => (string)v["value"] == "no_such_kind");
            Assert.Equal(Kinds.Values, unknown["valid_values"].Select(v => (string)v).ToList());
            Assert.Null(unknown["schema"]);
        }

        [Fact]
        public void An_error_without_structured_content_gains_only_a_line()
        {
            // A plain error now carries the stable cause as structure (error_code) - and
            // that structure REPEATS the message (`error`), so a reader that judges or
            // forwards structuredContent in place of the text (ProcedureRun; some clients)
            // still sees the error itself, not only the advice.
            JObject reply = McpResult.Text("export refused: unknown format.", true);
            JToken attached = SchemaHelp.Attach(reply, "horizun_export", new JObject { ["format"] = "no_such_format" });
            Assert.True((bool)attached["isError"]);
            Assert.Equal("esquema_invalido", (string)attached["structuredContent"]["error_code"]);
            Assert.Equal("export refused: unknown format.", (string)attached["structuredContent"]["error"]);
            string text = (string)attached["content"][0]["text"];
            Assert.StartsWith("export refused: unknown format." + Environment.NewLine + Environment.NewLine, text);
            Assert.Contains("error_code: esquema_invalido", text);
            Assert.Contains("Arguments violate the contract at /", text);
            // The structure now exists (it holds error_code), so the line points at it too.
            Assert.Contains("exact schema in structuredContent.schema_help (also horizun://contract/tools/horizun_export)", text);
            Assert.NotNull(attached["structuredContent"]["schema_help"]);
        }

        [Fact]
        public void The_error_line_names_the_schema_that_holds_the_first_violation()
        {
            var help = new JObject
            {
                ["contract_uri"] = "horizun://contract/tools/" + CreateElements,
                ["variants"] = new JArray(new JObject
                {
                    ["value"] = "wall", ["pointer"] = "/elements/1",
                    ["uri"] = "horizun://contract/tools/" + CreateElements + "/wall"
                })
            };
            // A missing top-level argument is in no variant's schema.
            Assert.EndsWith("(also horizun://contract/tools/" + CreateElements + ").",
                SchemaHelp.ErrorLine(help, new[] { "/: 'target_document' is required" }, true));
            // Inside the wall row, the wall variant; a sibling row with a longer index is not inside it.
            Assert.EndsWith("/wall).", SchemaHelp.ErrorLine(help, new[] { "/elements/1/height: must be number" }, true));
            Assert.EndsWith("(also horizun://contract/tools/" + CreateElements + ").",
                SchemaHelp.ErrorLine(help, new[] { "/elements/10: 'kind' is required" }, true));
            // Unstructured: the first violation itself, the count of the rest, and the uri.
            Assert.Equal("Arguments violate the contract at /elements/1/height: must be number (+1 more). Exact schema: " +
                         "horizun://contract/tools/" + CreateElements + "/wall.",
                SchemaHelp.ErrorLine(help, new[] { "/elements/1/height: must be number", "/x: y" }, false));
        }

        [Fact]
        public void A_session_save_without_its_target_gets_the_save_variant()
        {
            JObject args = new JObject { ["operation"] = "save" };
            Assert.NotEmpty(ContractSchemaCheck.Validate(args, Contract.Find("horizun_document_session").InputSchema));
            JObject reply = McpResult.Error("save refused.", null, null, new JObject { ["operation"] = "save" });
            JToken attached = SchemaHelp.Attach(reply, "horizun_document_session", args);
            Assert.Contains("(also horizun://contract/tools/horizun_document_session/save).",
                            (string)attached["content"][0]["text"]);
            JObject save = (JObject)Help(attached)["variants"].Single();
            Assert.Equal("save", (string)save["value"]);
            Assert.Equal("", (string)save["pointer"]);
            VariantSite site = ContractVariants.For("horizun_document_session").Single();
            Assert.True(JToken.DeepEquals(site.Branch("save"), save["schema"]));
        }

        [Fact]
        public void Beyond_three_kinds_only_the_uri_travels_and_the_caps_hold()
        {
            JObject[] rows = Kinds.Values.Take(6).Select(k => new JObject { ["kind"] = k }).ToArray();
            JToken attached = SchemaHelp.Attach(Rehearsal(0, rows.Length), CreateElements, Rows(rows));
            JObject help = Help(attached);
            var variants = help["variants"].OfType<JObject>().ToList();

            Assert.Equal(6, variants.Count);
            Assert.All(variants, v => Assert.NotNull(v["uri"]));
            var inline = variants.Where(v => v["schema"] != null).ToList();
            Assert.InRange(inline.Count, 1, SchemaHelp.MaxInlineSchemas);
            Assert.True(inline.Sum(v => Encoding.UTF8.GetByteCount(v["schema"].ToString(Formatting.None)))
                        <= SchemaHelp.MaxInlineSchemaBytes);
            Assert.True(Encoding.UTF8.GetByteCount(help.ToString(Formatting.None)) <= SchemaHelp.MaxBytes);
        }

        [Fact]
        public void Violations_are_bounded_in_count_length_and_total()
        {
            var many = Enumerable.Range(0, 100).Select(i => "/elements/" + i + ": " + new string('x', 900)).ToList();
            JToken attached = SchemaHelp.Attach(Rehearsal(0, 1), CreateElements, Rows(new JObject { ["kind"] = "wall" }),
                (a, s) => many);
            JObject help = Help(attached);
            var v = help["violations"].Select(x => (string)x).ToList();
            Assert.True(v.Count <= SchemaHelp.MaxViolations + 1);
            Assert.StartsWith("... ", v.Last());
            Assert.All(v, x => Assert.True(x.Length <= SchemaHelp.MaxViolationChars + 3));
            Assert.True(Encoding.UTF8.GetByteCount(help.ToString(Formatting.None)) <= SchemaHelp.MaxBytes);
        }

        [Fact]
        public void A_throwing_validator_leaves_the_result_unchanged()
        {
            JObject reply = Rehearsal(0, 1);
            string before = reply.ToString(Formatting.None);
            JToken attached = SchemaHelp.Attach(reply, CreateElements, Rows(new JObject { ["kind"] = "wall" }),
                (a, s) => throw new InvalidOperationException("validator bug"));
            Assert.Same(reply, attached);
            Assert.Equal(before, reply.ToString(Formatting.None));
        }
    }
}
