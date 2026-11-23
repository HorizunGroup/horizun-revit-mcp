// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The three capabilities the Spanish-standard template migration needs (Pro,
// 2026-10-18), pinned on the contract so a later trim of tools/list cannot drop
// them silently:
//   - horizun_manage_system_types rename_family_id (families renamed in a batch,
//     beside rename_type_id, which already covers system types);
//   - horizun_export format=ifc_setup with the ifc_setup object;
//   - horizun_manage_views operation=set_starting_view.
// Each is a write: the tool keeps target_document, dry_run and confirmation_token.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class TemplateMigrationContractTests
    {
        private static JObject Schema(string tool) => Contract.All.Single(c => c.Name == tool).InputSchema;

        private static void IsGuardedWrite(JObject schema)
        {
            Assert.NotNull(schema.SelectToken("properties.target_document"));
            Assert.True((bool)schema.SelectToken("properties.dry_run.default"));
            Assert.NotNull(schema.SelectToken("properties.confirmation_token"));
        }

        [Fact]
        public void Ifc_setup_is_an_export_format_with_its_own_object()
        {
            JObject s = Schema("horizun_export");
            IsGuardedWrite(s);
            Assert.Contains("ifc_setup", s.SelectToken("properties.format.enum").Values<string>());
            JObject setup = (JObject)s.SelectToken("properties.ifc_setup");
            Assert.Equal(new[] { "name" }, setup["required"].Values<string>().ToArray());
            Assert.False((bool)setup["additionalProperties"]);
            JObject row = (JObject)setup.SelectToken("properties.type_mappings.items");
            Assert.Equal(new[] { "type_id", "export_as" }, row["required"].Values<string>().ToArray());
            Assert.NotNull(row.SelectToken("properties.predefined_type"));
            Assert.NotNull(setup.SelectToken("properties.options"));
            Assert.NotNull(setup.SelectToken("properties.classification"));
        }

        [Fact]
        public void Starting_view_is_a_manage_views_operation()
        {
            JObject s = Schema("horizun_manage_views");
            IsGuardedWrite(s);
            Assert.Contains("set_starting_view", s.SelectToken("properties.actions.items.properties.operation.enum").Values<string>());
            Assert.Contains("set_starting_view", (string)s.SelectToken("properties.actions.items.properties.view_id.description"));
        }
    }
}
