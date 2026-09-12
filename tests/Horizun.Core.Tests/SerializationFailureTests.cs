using System;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;
using Horizun.Revit.Transport;
using Xunit;
namespace Horizun.Core.Tests
{
    public sealed class SerializationFailureTests
    {
        private sealed class Unserializable { public string Value => throw new InvalidOperationException("broken observer"); }
        [Fact]
        public void Original_Revit_error_survives_an_observer_serialization_failure()
        {
            var result=CommandResult.FailWithDetail("Revit rejected EndCap",new JObject { ["transaction_status"]="RolledBack",["write_started"]=true,["changes_applied"]=false });
            result.RevitSaid=new Unserializable();
            var wire=JObject.Parse(PipeEnvelope.Of("42",result).ToString());
            Assert.False((bool)wire["success"]);
            Assert.Equal("Revit rejected EndCap",(string)wire["detail"]["original_error"]);
            Assert.Equal("RolledBack",(string)wire["detail"]["transaction_status"]);
            Assert.False((bool)wire["detail"]["changes_applied"]);
        }
        [Fact]
        public void Unserializable_success_is_unknown_not_zero_changes()
        {
            var wire=PipeEnvelope.Of("43",CommandResult.Ok(new Unserializable()));
            Assert.False((bool)wire["success"]);
            Assert.Equal(JTokenType.Null,wire["detail"]["changes_applied"].Type);
        }
    }
}
