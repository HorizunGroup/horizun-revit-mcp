// -----------------------------------------------------------------------------
// Horizun Server tests — original Horizun code.
//
// EVERY ARGUMENT TEMPLATE, AGAINST THE SCHEMA OF THE TOOL IT NAMES.
//
// A procedure's steps are prose plus a template. The prose drifts silently — it is
// read by people, who fill in what it meant — and the template does not, because
// something dispatches it. But it only fails when the run REACHES that step, which
// on a nine-step route is after eight steps have run and some of them have written.
//
// All of it is decidable from the contract, with no Revit and nothing running: a
// key the tool does not declare, on a schema that refuses extras, makes the call
// invalid; a required key the template omits fails at dispatch. So the catalogue
// answers both per step, and this asserts the answer over every procedure at once.
//
// THE FOUR THINGS THAT MADE THIS WORTH WRITING, all found the same afternoon:
//
//   a template sent `operation` to horizun_query_model, which has no such property
//   — it filters and projects;
//
//   a template sent `target_document` to horizun_model_scan, which predates that
//   convention and declares `target_document_title`;
//
//   four acceptance checks tested INTEGER counts with `expect: "false"`, whose
//   predicate requires a Boolean — so a clean run would have failed all four;
//
//   and a procedure named horizun_execute_plan for a step whose planner hands you
//   horizun_manage_views instead, which had been true since it was written.
//
// WHAT IS DELIBERATELY NOT ASSERTED HERE: that a procedure's `Tools` array names
// exactly what its steps call. That array is a summary of what a route touches,
// and several entries list a tool a step may need without calling it every time -
// asserting equality would report a convention as a defect.
//
// NOT RUN in this phase. Written to be run when running tests is authorised.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class WorkflowTemplateAuditTests
    {
        [Fact]
        public void Every_argument_template_agrees_with_the_tool_it_names()
        {
            var wrong = new List<string>();
            foreach (McpWorkflowCatalog.Procedure procedure in McpWorkflowCatalog.Procedures)
                foreach (McpWorkflowCatalog.Step step in procedure.Steps ?? new McpWorkflowCatalog.Step[0])
                {
                    JObject audit = step.TemplateAudit();
                    if (audit == null) continue;                       // no template: documented, not executable
                    if (audit.Value<bool?>("agrees") == true) continue;

                    wrong.Add(procedure.Id + " step " + step.N + " (" + step.Tool + "): " +
                              (audit["keys_the_tool_does_not_declare"] == null
                                  ? ""
                                  : "sends " + audit["keys_the_tool_does_not_declare"] + " ") +
                              (audit["required_keys_the_template_omits"] == null
                                  ? ""
                                  : "omits " + audit["required_keys_the_template_omits"]) +
                              (audit.Value<string>("reason") ?? ""));
                }

            Assert.True(wrong.Count == 0,
                "Templates that disagree with their tool's schema:\n  " + string.Join("\n  ", wrong));
        }

        [Fact]
        public void Every_placeholder_in_a_template_uses_the_resolver_vocabulary()
        {
            // The resolver reads exactly these, and ignores anything else in the
            // holder - so a marker it does not implement reads as a promise the
            // template does not keep. `from_step` belongs to `$decision`, and
            // `step` and `path` to `$ref`.
            var known = new HashSet<string>(StringComparer.Ordinal)
            {
                "$input", "$decision", "$ref", "from_step", "step", "path",

                // when_missing says what to do when an OPTIONAL input is not in the
                // run: fail (the default), omit the property, or send it as null.
                // It was added because a template could not express an argument
                // that only some runs supply - the outfall on a drainage
                // conversion - and the procedure had to ask for it in prose.
                "when_missing"
            };

            var strange = new List<string>();
            foreach (McpWorkflowCatalog.Procedure procedure in McpWorkflowCatalog.Procedures)
                foreach (McpWorkflowCatalog.Step step in procedure.Steps ?? new McpWorkflowCatalog.Step[0])
                {
                    if (string.IsNullOrWhiteSpace(step.ArgumentsJson)) continue;
                    JObject template;
                    try { template = JObject.Parse(step.ArgumentsJson); }
                    catch (Exception ex)
                    {
                        strange.Add(procedure.Id + " step " + step.N + ": template is not JSON - " + ex.Message);
                        continue;
                    }

                    foreach (JObject holder in template.Descendants().OfType<JObject>())
                    {
                        // Only objects that mean to be placeholders are judged: a
                        // template may carry an ordinary nested object.
                        if (!holder.Properties().Any(x => x.Name.StartsWith("$", StringComparison.Ordinal)))
                            continue;

                        foreach (JProperty property in holder.Properties())
                            if (!known.Contains(property.Name))
                                strange.Add(procedure.Id + " step " + step.N + ": placeholder carries '" +
                                            property.Name + "', which the resolver does not read.");
                    }
                }

            Assert.True(strange.Count == 0,
                "Placeholders using something the resolver does not implement:\n  " +
                string.Join("\n  ", strange));
        }

        [Fact]
        public void Every_step_names_a_tool_the_contract_carries()
        {
            var unknown = new List<string>();
            foreach (McpWorkflowCatalog.Procedure procedure in McpWorkflowCatalog.Procedures)
                foreach (McpWorkflowCatalog.Step step in procedure.Steps ?? new McpWorkflowCatalog.Step[0])
                    if (Horizun.Contracts.Contract.Find(step.Tool) == null)
                        unknown.Add(procedure.Id + " step " + step.N + ": " + step.Tool);

            Assert.True(unknown.Count == 0,
                "Steps naming a tool the contract does not carry:\n  " + string.Join("\n  ", unknown));
        }

        [Fact]
        public void Every_acceptance_check_uses_an_expectation_this_build_evaluates()
        {
            // THE ONE THAT CAUGHT FOUR AT ONCE. `expect: "false"` requires a
            // Boolean, so an integer count of zero fails it - and four checks
            // written against stages_failed, refused, not_made and
            // open_connectors_total would have reported every clean run as
            // unacceptable, on exactly the routes whose value is being trustworthy
            // about whether the model is finished.
            var bad = new List<string>();
            foreach (McpWorkflowCatalog.Procedure procedure in McpWorkflowCatalog.Procedures)
                foreach (McpWorkflowCatalog.AcceptanceCheck check in
                         procedure.Checks ?? new McpWorkflowCatalog.AcceptanceCheck[0])
                    if (!check.IsEvaluable)
                        bad.Add(procedure.Id + " step " + check.Step + ": expect '" + check.Expect + "'");

            Assert.True(bad.Count == 0,
                "Acceptance checks this build can never evaluate:\n  " + string.Join("\n  ", bad) +
                "\nKnown: " + string.Join(", ", McpWorkflowCatalog.AcceptanceCheck.Evaluable));
        }

        [Fact]
        public void Every_acceptance_check_explains_itself()
        {
            // A check that fails and cannot say what it was for sends somebody to
            // read the source of a catalogue to find out what their run violated.
            foreach (McpWorkflowCatalog.Procedure procedure in McpWorkflowCatalog.Procedures)
                foreach (McpWorkflowCatalog.AcceptanceCheck check in
                         procedure.Checks ?? new McpWorkflowCatalog.AcceptanceCheck[0])
                {
                    Assert.False(string.IsNullOrWhiteSpace(check.Path), procedure.Id + ": a check with no path");
                    Assert.False(string.IsNullOrWhiteSpace(check.Why),
                                 procedure.Id + " step " + check.Step + ": a check with no reason");
                }
        }

        [Fact]
        public void A_procedure_is_executable_exactly_when_it_can_be_dispatched()
        {
            // The catalogue publishes `detail`, and a client plans from it. It must
            // mean what it says: executable and then blocked on the first step is
            // worse than honestly labelled documentation.
            foreach (McpWorkflowCatalog.Procedure procedure in McpWorkflowCatalog.Procedures)
            {
                bool everyStepTemplated = (procedure.Steps ?? new McpWorkflowCatalog.Step[0])
                    .All(x => x.ArgumentsJson != null);
                bool hasSteps = procedure.Steps != null && procedure.Steps.Length > 0;
                bool hasSchema = procedure.InputSchemaJson != null;

                string expected = !hasSteps ? "tool_list_only"
                    : (hasSchema && everyStepTemplated) ? "executable" : "steps";
                Assert.Equal(expected, procedure.Detail);
            }
        }

        [Fact]
        public void A_step_that_requires_a_decision_says_what_the_decision_is()
        {
            // "This step needs somebody" is not an instruction. The person has to
            // know WHAT to supply, or the run stops and nobody can restart it.
            foreach (McpWorkflowCatalog.Procedure procedure in McpWorkflowCatalog.Procedures)
                foreach (McpWorkflowCatalog.Step step in procedure.Steps ?? new McpWorkflowCatalog.Step[0])
                    if (step.RequiresDecision)
                        Assert.False(string.IsNullOrWhiteSpace(step.DecisionNeeded),
                                     procedure.Id + " step " + step.N + " stops for a decision it does not name");
        }

        [Fact]
        public void Every_input_schema_is_json_and_refuses_what_it_does_not_declare()
        {
            // additionalProperties:false is what makes a misspelt input a refusal
            // instead of a value nobody reads.
            foreach (McpWorkflowCatalog.Procedure procedure in McpWorkflowCatalog.Procedures)
            {
                if (procedure.InputSchemaJson == null) continue;
                JObject schema = JObject.Parse(procedure.InputSchemaJson);
                Assert.Equal("object", schema.Value<string>("type"));
                Assert.NotNull(schema["properties"]);
                Assert.Equal(false, schema.Value<bool?>("additionalProperties"));
            }
        }

        [Fact]
        public void Every_required_input_is_a_property_the_schema_declares()
        {
            // A required name that is not in `properties` is a name nobody can
            // discover, and Start refuses the run for a key the caller was never
            // told about.
            foreach (McpWorkflowCatalog.Procedure procedure in McpWorkflowCatalog.Procedures)
            {
                if (procedure.InputSchemaJson == null) continue;
                JObject schema = JObject.Parse(procedure.InputSchemaJson);
                var properties = schema["properties"] as JObject;
                foreach (JToken required in schema["required"] as JArray ?? new JArray())
                    Assert.True(properties != null && properties[(string)required] != null,
                                procedure.Id + " requires '" + required + "' and does not declare it");
            }
        }

        [Fact]
        public void Every_template_hole_names_an_input_the_schema_declares()
        {
            // A `$input` that the schema does not declare blocks the step it is on,
            // with a message about a missing input the caller had no way to know
            // they should have sent.
            var dangling = new List<string>();
            foreach (McpWorkflowCatalog.Procedure procedure in McpWorkflowCatalog.Procedures)
            {
                if (procedure.InputSchemaJson == null) continue;
                var properties = JObject.Parse(procedure.InputSchemaJson)["properties"] as JObject;

                foreach (McpWorkflowCatalog.Step step in procedure.Steps ?? new McpWorkflowCatalog.Step[0])
                {
                    if (step.ArgumentsJson == null) continue;
                    foreach (string name in InputNames(JObject.Parse(step.ArgumentsJson)))
                        if (properties == null || properties[name] == null)
                            dangling.Add(procedure.Id + " step " + step.N + " uses input '" + name +
                                         "', which its schema does not declare");
                }
            }

            Assert.True(dangling.Count == 0,
                "Template holes with no matching input:\n  " + string.Join("\n  ", dangling));
        }

        [Fact]
        public void Every_reference_points_at_a_step_that_runs_before_it()
        {
            // A $ref to a later step, or to itself, can never resolve: the executor
            // refuses it with "step N has not run yet", every time, forever.
            var backwards = new List<string>();
            foreach (McpWorkflowCatalog.Procedure procedure in McpWorkflowCatalog.Procedures)
                foreach (McpWorkflowCatalog.Step step in procedure.Steps ?? new McpWorkflowCatalog.Step[0])
                {
                    if (step.ArgumentsJson == null) continue;
                    foreach (int from in ReferencedSteps(JObject.Parse(step.ArgumentsJson)))
                        if (from >= step.N)
                            backwards.Add(procedure.Id + " step " + step.N + " references step " + from);
                }

            Assert.True(backwards.Count == 0,
                "References that can never resolve:\n  " + string.Join("\n  ", backwards));
        }

        // ---------------------------------------------------------------------
        private static IEnumerable<string> InputNames(JToken token)
        {
            var holder = token as JObject;
            if (holder != null)
            {
                JToken input = holder["$input"];
                if (input != null) { yield return (string)input; yield break; }
                foreach (JProperty property in holder.Properties())
                    foreach (string name in InputNames(property.Value)) yield return name;
                yield break;
            }
            var array = token as JArray;
            if (array == null) yield break;
            foreach (JToken item in array)
                foreach (string name in InputNames(item)) yield return name;
        }

        private static IEnumerable<int> ReferencedSteps(JToken token)
        {
            var holder = token as JObject;
            if (holder != null)
            {
                var reference = holder["$ref"] as JObject;
                if (reference != null)
                {
                    int? from = reference.Value<int?>("step");
                    if (from.HasValue) yield return from.Value;
                    yield break;
                }
                var decision = holder["$decision"];
                if (decision != null)
                {
                    int? from = holder.Value<int?>("from_step");
                    if (from.HasValue) yield return from.Value;
                    yield break;
                }
                foreach (JProperty property in holder.Properties())
                    foreach (int from in ReferencedSteps(property.Value)) yield return from;
                yield break;
            }
            var array = token as JArray;
            if (array == null) yield break;
            foreach (JToken item in array)
                foreach (int from in ReferencedSteps(item)) yield return from;
        }
    }
}
