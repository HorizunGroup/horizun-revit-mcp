using System;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;
namespace Horizun.Core.Tests
{
    public sealed class SourceTraceTests
    {
        private static JObject Reference() => new JObject
        {
            ["document_id"]="fixture.pdf",["document_sha256"]=new string('a',64),["page"]=2,["method"]="dimension",
            ["measurements"]=new JArray(new JObject { ["property"]="height",["value"]=3048,["unit"]="mm",["tolerance"]=1,["reference"]="Dimension A" })
        };
        [Fact]
        public void Reference_units_are_compared_with_measured_geometry()
        {
            var check=new PostconditionCheck("height").Measure("height",10,10,1e-6,"feet","face");
            Assert.True(SourceTrace.Compare(Reference(),check.ToJson()).Value<bool>("matches"));
            var wrong=new PostconditionCheck("height").Measure("height",8,8,1e-6,"feet","face");
            Assert.False(SourceTrace.Compare(Reference(),wrong.ToJson()).Value<bool>("matches"));
        }
        [Fact]
        public void Unmeasured_dimension_is_not_fidelity()
        {
            var check=new PostconditionCheck("kind").Compare("kind","Wall","Wall");
            var comparison=SourceTrace.Compare(Reference(),check.ToJson());
            Assert.False(comparison.Value<bool>("matches"));
            Assert.Equal("unmeasured_or_incompatible",(string)comparison["measurements"][0]["status"]);
        }
        [Fact]
        public void Inference_requires_its_assumption_and_page_is_one_based()
        {
            var trace=Reference(); trace["method"]="scaled_measurement";
            Assert.Throws<ArgumentException>(()=>SourceTrace.Validate(trace));
            trace["assumption"]="Scale verified using dimension A"; SourceTrace.Validate(trace);
            trace["page"]=0; Assert.Throws<ArgumentException>(()=>SourceTrace.Validate(trace));
        }
    }
}
