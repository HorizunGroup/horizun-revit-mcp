using System.IO;
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ConditionalFieldCycleTests
    {
        [Theory]
        [InlineData("horizun_save_document")]
        [InlineData("horizun_document_session")]
        public void Operation_dependent_dry_run_has_no_global_default(string tool)
        {
            Assert.Null(Contract.Find(tool).InputSchema.SelectToken("properties.dry_run.default"));
        }
        [Theory]
        [InlineData("{'operation':'override_report','element_ids':[1]}", false)]
        [InlineData("{'operation':'override_report','view_ids':[2],'element_ids':[1]}", true)]
        public void Override_report_requires_view_ids_in_the_schema(string json, bool valid)
            => Check("horizun_view_visibility", json, valid);

        private static void Check(string tool, string json, bool valid)
        {
            var errors = ContractSchemaCheck.Validate(JObject.Parse(json), Contract.Find(tool).InputSchema);
            Assert.Equal(valid, errors.Count == 0);
        }

        [Theory]
        [InlineData("{'target_document':'A','mode':'floating'}", false)]
        [InlineData("{'target_document':'A','mode':'floating','categories':[]}", false)]
        [InlineData("{'target_document':'A','mode':'floating','categories':['OST_Walls']}", true)]
        [InlineData("{'target_document':'A','mode':'coverage_gaps'}", false)]
        [InlineData("{'target_document':'A','mode':'coverage_gaps','level_id':1}", true)]
        [InlineData("{'target_document':'A','mode':'coverage_gaps','room_ids':[2]}", true)]
        [InlineData("{'target_document':'A','mode':'model_offset'}", false)]
        [InlineData("{'target_document':'A','mode':'model_offset','link_instance_id':1}", true)]
        public void Support_mode_requirements_are_available_to_kit_validation(string json, bool valid)
            => Check("horizun_audit_support", json, valid);

        [Theory]
        [InlineData("{'operation':'read'}", false)]
        [InlineData("{'operation':'read','element_ids':[1,2]}", true)]
        [InlineData("{'operation':'read','pairs':[[1,2]]}", true)]
        [InlineData("{'operation':'read','pairs':[[1,2]],'element_ids':[1,2]}", false)]
        [InlineData("{'operation':'auto_priority','target_document':'A','level_id':1}", false)]
        [InlineData("{'operation':'auto_priority','target_document':'A','level_id':1,'priority':['wall'],'verification_mode':'per_pair'}", true)]
        [InlineData("{'operation':'join','pairs':[[1,2]]}", false)]
        [InlineData("{'operation':'fix_corners','target_document':'A'}", false)]
        public void Join_requires_an_explicit_scope_before_a_recipe_can_be_valid(string json, bool valid)
            => Check("horizun_join_geometry", json, valid);

        [Fact]
        public void Relinquish_rehearsal_is_accepted_by_contract_and_dispatch()
        {
            var args = JObject.Parse("{'operation':'relinquish_own','dry_run':true,'target_document':'Example'}");
            Assert.Empty(ContractSchemaCheck.Validate(args, Contract.Find("horizun_save_document").InputSchema));
            Assert.Empty(UndeclaredArguments.Find("horizun_save_document", args));
            Assert.True(ToolInputRules.IsSaveDocumentRehearsal("horizun_save_document", args));
            Assert.True(ToolInputRules.IsSaveDocumentRehearsal("horizun_save_document", JObject.Parse("{'operation':'relinquish_own'}")));
            Assert.True(ToolInputRules.IsSaveDocumentRehearsal("horizun_save_document", JObject.Parse("{'operation':'sync_with_central'}")));
            Assert.False(ToolInputRules.IsSaveDocumentRehearsal("horizun_save_document", JObject.Parse("{'operation':'relinquish_own','dry_run':false}")));
            Assert.False(ToolInputRules.IsSaveDocumentRehearsal("horizun_save_document", JObject.Parse("{'operation':'save_local','dry_run':true}")));
            Assert.False(ToolInputRules.IsSaveDocumentRehearsal("horizun_export", args));
        }

        [Theory]
        [InlineData("{'operation':'save_local','target_document':'Example','idempotency_key':'save-1'}")]
        [InlineData("{'operation':'sync_with_central','target_document':'Example','comment':'Done','dry_run':true}")]
        [InlineData("{'operation':'relinquish_own','target_document':'Example','dry_run':true}")]
        public void Save_document_operations_accept_their_declared_arguments(string json)
        {
            var args = JObject.Parse(json);
            Assert.Empty(UndeclaredArguments.Find("horizun_save_document", args));
            Assert.Empty(ContractSchemaCheck.Validate(args, Contract.Find("horizun_save_document").InputSchema));
        }

    }
}
