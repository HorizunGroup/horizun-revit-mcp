using System.Collections.Generic;
using System.Text;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanPreviewTests
    {
        [Fact] public void Preview_keeps_before_and_proposed_separate_and_uses_the_confirmation_fingerprint()
        {
            var plan=new ResolvedPlan { Command="test",DocumentKey="doc" };
            plan.Elements.Add(new PlannedElement {UniqueId="uid",ElementId=10,Action=PlannedAction.Modify,
                BeforeValues=new Dictionary<string,string>{{"pinned","False"}},
                ProposedValues=new Dictionary<string,string>{{"pinned","True"}} });
            var preview=PlanPreview.Describe(plan);
            Assert.Equal(plan.Fingerprint(),(string)preview["fingerprint"]);
            Assert.Equal("False",(string)preview["rows"][0]["captured_state"]["pinned"]);
            Assert.Equal("True",(string)preview["rows"][0]["proposed_values"]["pinned"]);
            string old=plan.Fingerprint(); plan.Elements[0].ProposedValues["pinned"]="False";
            Assert.NotEqual(old,plan.Fingerprint());
        }
        [Fact] public void Omitted_rows_and_clipped_values_are_still_bound_by_the_full_fingerprint()
        {
            var plan=new ResolvedPlan { Command="test",DocumentKey="doc" };
            for(int i=0;i<500;i++) plan.Elements.Add(new PlannedElement { UniqueId=i.ToString("D4"),Action=PlannedAction.Modify,
                ProposedValues=new Dictionary<string,string>{{"value",new string('x',10000)}} });
            var preview=PlanPreview.Describe(plan);
            Assert.True((bool)preview["truncated"]); Assert.Equal(500,(int)preview["total"]);
            Assert.True((bool)preview["rows"][0]["values_truncated"]);
            Assert.True(Encoding.UTF8.GetByteCount(preview.ToString(Formatting.None))<PlanPreview.MaxBytes);
            string before=plan.Fingerprint();plan.Elements[499].ProposedValues["value"]+="y";
            Assert.NotEqual(before,plan.Fingerprint());
        }
        [Fact] public void Missing_proposed_values_are_not_invented_and_long_keys_do_not_collide()
        {
            var plan=new ResolvedPlan();
            plan.Elements.Add(new PlannedElement { UniqueId="uid",BeforeValues=new Dictionary<string,string> {
                {new string('k',300)+"a","first"},{new string('k',300)+"b","second"},{"normal","kept"} } });
            var row=PlanPreview.Describe(plan)["rows"][0];
            Assert.Equal(JTokenType.Null,row["proposed_values"].Type);
            Assert.Equal("kept",(string)row["captured_state"]["normal"]);
            Assert.Single(((JObject)row["captured_state"]).Properties());
            Assert.True((bool)row["values_truncated"]);
        }
    }
}
