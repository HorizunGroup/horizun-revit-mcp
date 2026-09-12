using System;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WorkflowPlanTests
    {
        static JObject Expand(string json)=>WorkflowPlan.Expand(JObject.Parse(json));
        [Fact] public void Sheet_workflow_binds_view_template_titleblock_and_placement_in_one_typed_batch()
        {
            var request=JObject.Parse(@"{'target_document':'Fixture','workflow':{'name':'prepare_sheet_set','sheets':[
              {'view_id':10,'template_view_id':20,'number':'A01','name':'Ground','title_block_type_id':30,'point':[200,150]}]}}");
            var result=WorkflowPlan.Expand(request);
            Assert.Null(request["actions"]);
            Assert.Equal("horizun_manage_views",(string)result["actions"][0]["tool"]);
            var actions=(JArray)result["actions"][0]["arguments"]["actions"];
            Assert.Equal(3,actions.Count);
            Assert.Equal("apply_template",(string)actions[0]["operation"]);
            Assert.Equal(20,(int)actions[0]["template_view_id"]);
            Assert.Equal(30,(int)actions[1]["title_block_type_id"]);
            Assert.Equal(actions[1]["key"],actions[2]["sheet_key"]);
            Assert.Equal(10,(int)actions[2]["view_id"]);
            Assert.True(JToken.DeepEquals(request["workflow"]["sheets"][0]["point"],actions[2]["point"]));
        }
        [Theory]
        [InlineData(@"{'name':'pin_elements','element_ids':[10,10]}")]
        [InlineData(@"{'name':'apply_view_template','view_ids':[10]}")]
        [InlineData(@"{'name':'prepare_sheet_set','sheets':[{'view_id':10,'number':'A','name':'A','title_block_type_id':20}]}")]
        [InlineData(@"{'name':'pin_elements','element_ids':[10],'unknown':true}")]
        [InlineData(@"{'name':'prepare_sheet_set','sheets':[{'view_id':10,'number':'A','name':'A','title_block_type_id':20,'point':[200,150,10]}]}")]
        public void Incomplete_or_ambiguous_intent_is_refused(string workflow)
        { Assert.Throws<ArgumentException>(()=>Expand("{'workflow':"+workflow+"}")); }
        [Fact] public void Workflow_and_raw_actions_are_mutually_exclusive()
        { Assert.Throws<ArgumentException>(()=>Expand(@"{'actions':[],'workflow':{'name':'pin_elements','element_ids':[10]}}")); }
        [Fact] public void Changed_targets_change_the_existing_confirmation_hash()
        {
            var a=Expand(@"{'workflow':{'name':'pin_elements','element_ids':[10]}}");
            var b=Expand(@"{'workflow':{'name':'pin_elements','element_ids':[11]}}");
            Assert.NotEqual(ConfirmationStore.PlanHash(a,"actions"),ConfirmationStore.PlanHash(b,"actions"));
        }
    }
}
