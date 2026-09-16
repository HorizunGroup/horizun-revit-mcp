// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A FIELD IN THE TABLE WITH NO PROPERTY IN THE SCHEMA TAKES THE WHOLE BRIDGE DOWN.
//
// MEASURED: adding "join_rule" to the wall's entry in ToolInputRules.CreationFields
// - and nothing else - made Revit start with NO add-in at all:
//
//     STARTUP FAILED - no discovery file was written, so no MCP client can connect.
//     TypeInitializationException: Horizun.Contracts.Contract
//     NullReferenceException at Contract.PluginCommands
//
// because AddCreationVariants clones each named field out of the element schema's
// properties, and a name with no property there is a null being dereferenced in a
// static initialiser. Every tool in the process disappears, and the only trace is
// a line in Revit's journal.
//
// The suite never saw it: the tests that use CreationFields do not touch
// Contract.All, and the tests that touch Contract.All do not add fields. This is
// the case that joins them.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CreationFieldSchemaTests
    {
        /// <summary>The element schema horizun_create_elements publishes, after its variants are built.</summary>
        private static JObject ElementProperties()
        {
            CommandContract create = Contract.Find("horizun_create_elements");
            Assert.NotNull(create);
            var items = (JObject)create.InputSchema["properties"]["elements"]["items"];
            return (JObject)items["properties"];
        }

        [Fact]
        public void Every_field_a_kind_accepts_is_declared_in_the_element_schema()
        {
            JObject props = ElementProperties();
            var missing = new List<string>();

            foreach (KeyValuePair<string, string[]> kind in ToolInputRules.CreationFields)
                foreach (string field in kind.Value)
                    if (props[field] == null)
                        missing.Add(kind.Key + " -> " + field);

            Assert.True(missing.Count == 0,
                "these fields are accepted by ValidateCreation and have no property in the element schema, " +
                "which is a NullReferenceException in Contract's static initialiser and a Revit that loads no " +
                "add-in: " + string.Join(", ", missing));
        }

        /// <summary>
        /// The contract must be constructible at all. This is the cheapest possible
        /// guard on the failure above, and it belongs beside it: every other test
        /// that touches Contract passes through the same initialiser, but only by
        /// accident of what it was asking about.
        /// </summary>
        [Fact]
        public void The_contract_initialises_and_names_its_plugin_commands()
        {
            List<string> commands = Contract.PluginCommands.ToList();

            Assert.NotEmpty(commands);
            Assert.All(commands, c => Assert.False(string.IsNullOrWhiteSpace(c)));
            Assert.Equal(commands.Count, commands.Distinct().Count());
        }
    }
}
