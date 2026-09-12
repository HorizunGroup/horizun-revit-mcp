using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class TransportFailureDetailTests
    {
        [Fact]
        public async Task Status_control_reports_the_same_request_while_its_main_reply_is_pending()
        {
            string name="hrz-observe-test-"+Guid.NewGuid().ToString("N");
            var observed=new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            using(var main=new NamedPipeServerStream(name,PipeDirection.InOut,2,PipeTransmissionMode.Byte,PipeOptions.Asynchronous))
            using(var control=new NamedPipeServerStream(name,PipeDirection.InOut,2,PipeTransmissionMode.Byte,PipeOptions.Asynchronous))
            {
                var peer=Task.Run(async()=>
                {
                    using(var timeout=new CancellationTokenSource(15000))
                    {
                        await main.WaitForConnectionAsync(timeout.Token);
                        var request=JObject.Parse(await new StreamReader(main).ReadLineAsync());
                        await control.WaitForConnectionAsync(timeout.Token);
                        var status=JObject.Parse(await new StreamReader(control).ReadLineAsync());
                        Assert.Equal("__horizun_request_status",(string)status["command"]);
                        Assert.Equal((string)request["id"],(string)status["params"]["wire_id"]);
                        var writer=new StreamWriter(control) { AutoFlush=true };
                        await writer.WriteLineAsync(new JObject { ["id"]=status["id"],["success"]=true,["data"]=new JObject {
                            ["wire_id"]=request["id"],["state"]="executing",["python_runtime"]=new JObject { ["state"]="warming" } } }.ToString(Newtonsoft.Json.Formatting.None));
                        await observed.Task.WaitAsync(timeout.Token);
                        await new StreamWriter(main) { AutoFlush=true }.WriteLineAsync(new JObject { ["id"]=request["id"],["success"]=true,["data"]=new JObject() }.ToString(Newtonsoft.Json.Formatting.None));
                    }
                });
                var result=PipeClient.Send(new Discovered { PipeName=name },"fixture",new JObject(),12000,observe:s=>observed.TrySetResult(s));
                await peer;
                var statusObserved=await observed.Task;
                Assert.True((bool)result["success"]);
                Assert.Equal("executing",(string)statusObserved["state"]);
                Assert.Equal("warming",(string)statusObserved["python_runtime"]["state"]);
            }
        }

        [Fact]
        public void Cancelled_before_connect_proves_nothing_was_submitted()
        {
            var target = new Discovered { PipeName = "unused-" + Guid.NewGuid().ToString("N") };
            var ex = Assert.Throws<OperationCanceledException>(() => PipeClient.Send(target, "horizun_create_elements", new JObject(), 1000, new CancellationToken(true)));
            var detail = (JObject)ex.Data["horizun_transport_detail"];
            Assert.False((bool)detail["changes_applied"]);
            Assert.False((bool)detail["request_may_have_been_submitted"]);
            Assert.Equal("not_started", (string)detail["transaction_status"]);
            Assert.False(string.IsNullOrEmpty((string)detail["correlation_id"]));
        }

        [Fact]
        public async Task Lost_reply_keeps_write_state_unknown_and_preserves_wire_id_through_mcp()
        {
            string name = "hrz-transport-test-" + Guid.NewGuid().ToString("N");
            using (var peer = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                var requestRead = Task.Run(async () =>
                {
                    await peer.WaitForConnectionAsync();
                    using (var reader = new StreamReader(peer))
                    {
                        string request = await reader.ReadLineAsync();
                        peer.Disconnect(); // The operation's answer is lost, not evidence of rollback.
                        return JObject.Parse(request);
                    }
                });
                var ex = Assert.Throws<IOException>(() => PipeClient.Send(new Discovered { PipeName = name },
                    "horizun_create_elements", new JObject(), 5000));
                var request = await requestRead;
                var detail = (JObject)ex.Data["horizun_transport_detail"];
                var mcp = McpResult.Error(ex.Message, null, null, detail);
                var wire = JObject.Parse(mcp.ToString());
                Assert.Equal((string)request["id"], (string)wire["structuredContent"]["correlation_id"]);
                Assert.True((bool)wire["structuredContent"]["request_may_have_been_submitted"]);
                Assert.Equal(JTokenType.Null, wire["structuredContent"]["changes_applied"].Type);
                Assert.Equal("unknown", (string)wire["structuredContent"]["transaction_status"]);
            }
        }
    }
}
