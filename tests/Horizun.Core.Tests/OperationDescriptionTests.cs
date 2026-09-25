using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // Field report, 2026-09-25: the operations pane said "failed" on every row and
    // "horizun_execute_python" as the only description.
    public class OperationDescriptionTests
    {
        private static JObject R(string json) => JObject.Parse(json);

        [Fact]
        public void The_outcome_the_ledger_writes_is_what_decides_success()
        {
            Assert.True(OperationDescription.Succeeded(R("{\"outcome\":\"ok\"}")));
            Assert.False(OperationDescription.Succeeded(R("{\"outcome\":\"failed\"}")));
            // Older lines carried success; still honoured.
            Assert.True(OperationDescription.Succeeded(R("{\"success\":true}")));
            // The receipt the ledger builds today is read as a success - the defect was that it was not.
            JObject built = ReceiptLedger.Build("horizun_query_model", true, null, new JObject(), 1, 2, "1", System.DateTime.UtcNow);
            Assert.Equal(OperationDescription.Read, OperationDescription.Kind(built));
        }

        [Fact]
        public void A_write_that_changed_the_model_says_what_it_changed_in_words()
        {
            var r = R("{\"tool\":\"horizun_create_elements\",\"outcome\":\"ok\",\"transaction_status\":\"Committed\",\"model_changes\":{\"added\":3,\"modified\":1,\"deleted\":0}}");
            Assert.Equal(OperationDescription.Changed, OperationDescription.Kind(r));
            Assert.True(OperationDescription.ShownByDefault(r));
            string es = OperationDescription.Sentence(r, true);
            Assert.StartsWith("Crear elementos", es);
            Assert.Contains("3 añadidos, 1 modificados, 0 borrados", es);
            Assert.Contains("3 added", OperationDescription.Sentence(r, false));
        }

        [Fact]
        public void A_python_script_is_described_by_its_purpose_not_the_tool_id()
        {
            var r = R("{\"tool\":\"horizun_execute_python\",\"outcome\":\"ok\",\"purpose\":\"renombra 12 niveles al estándar\",\"model_changes\":{\"added\":0,\"modified\":12,\"deleted\":0}}");
            string s = OperationDescription.Sentence(r, true);
            Assert.StartsWith("renombra 12 niveles al estándar", s);
            Assert.DoesNotContain("horizun_execute_python", s);
            // Without a purpose it is still a phrase, never the bare id.
            Assert.StartsWith("Script de Python", OperationDescription.Sentence(R("{\"tool\":\"horizun_execute_python\",\"outcome\":\"ok\"}"), true));
        }

        [Fact]
        public void Reads_and_rehearsals_are_hidden_by_default_and_failures_are_not()
        {
            Assert.False(OperationDescription.ShownByDefault(R("{\"tool\":\"horizun_query_model\",\"outcome\":\"ok\"}")));
            var dry = R("{\"tool\":\"horizun_create_elements\",\"outcome\":\"ok\",\"dry_run\":true}");
            Assert.Equal(OperationDescription.Rehearsal, OperationDescription.Kind(dry));
            Assert.False(OperationDescription.ShownByDefault(dry));
            var bad = R("{\"tool\":\"horizun_create_elements\",\"outcome\":\"failed\",\"error\":\"type_id must identify a FamilySymbol\"}");
            Assert.True(OperationDescription.ShownByDefault(bad));
            Assert.Contains("no se hizo: type_id must identify", OperationDescription.Sentence(bad, true));
        }

        [Fact]
        public void A_spatial_finding_rides_on_the_sentence()
        {
            var r = R("{\"tool\":\"horizun_create_elements\",\"outcome\":\"ok\",\"model_changes\":{\"added\":1},\"spatial_check\":{\"errors\":1,\"warnings\":2}}");
            Assert.Contains("revisión espacial: 1 errores, 2 avisos", OperationDescription.Sentence(r, true));
        }

        [Fact]
        public void The_receipt_keeps_only_short_human_fields_of_the_request()
        {
            var reply = R("{\"model_changes\":{\"added\":1,\"modified\":0,\"deleted\":0},\"spatial_check\":{\"status\":\"conflicts\",\"errors\":1,\"warnings\":0,\"findings\":[{\"x\":1}]},\"attention\":\"Spatial check: 1 error\"}");
            var request = R("{\"operation\":\"set_panel_type\",\"purpose\":\"cambia un panel\",\"code\":\"print('secret payload')\"}");
            JObject r = ReceiptLedger.Build("horizun_manage_curtain", true, null, reply, 1, 2, "9", System.DateTime.UtcNow, request);
            Assert.Equal("set_panel_type", (string)r["request_operation"]);
            Assert.Equal("cambia un panel", (string)r["purpose"]);
            Assert.Null(r["code"]);
            Assert.Null(r["spatial_check"]["findings"]);
            Assert.Equal(1, (int)r["spatial_check"]["errors"]);
            Assert.Equal(1, (int)r["model_changes"]["added"]);
        }
    }
}
