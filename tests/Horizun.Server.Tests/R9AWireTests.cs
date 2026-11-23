using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public class R9AWireTests
    {
        [Theory]
        [InlineData("horizun_audit_model", "{'target_document':'Model A','operation':'warnings','offset':0,'page_size':10,'byte_budget':4096}")]
        [InlineData("horizun_measure_geometry", "{'target_document':'Model A','ids':[1],'measures':['summary'],'offset':0,'page_size':10}")]
        [InlineData("horizun_query_planimetry", "{'mode':'unplaced_views','target_document':'Model A','max_rows':5}")]
        [InlineData("horizun_query_dimensions", "{'target_document':'Model A','max_rows':5}")]
        [InlineData("horizun_list_schedules", "{'target_document':'Model A'}")]
        [InlineData("horizun_get_schedule_data", "{'target_document':'Model A','schedule_id':1}")]
        [InlineData("horizun_query_detail_2d", "{'target_document':'Model A','view_id':1}")]
        [InlineData("horizun_audit_planimetry", "{'target_document':'Model A'}")]
        public async Task Request_crosses_schema_server_pipe_and_dispatch(string tool, string json)
        {
            JObject args = JObject.Parse(json);
            string name = "hrz-r9a-" + Guid.NewGuid().ToString("N");
            string directory = Path.Combine(Path.GetTempPath(), name);
            Directory.CreateDirectory(directory);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            string root = AppContext.BaseDirectory;
            string configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Name;
            while (!File.Exists(Path.Combine(root, "src", "Horizun.Server", "Horizun.Server.csproj")))
                root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Repository root not found");
            // Other wire tests load this same Release assembly into the default context.
            var server = Assembly.LoadFrom(Path.Combine(root, "src", "Horizun.Server", "bin", "Release", "net8.0", "horizun-mcp.dll"));
            Type pipeClient = server.GetType("Horizun.Server.PipeClient", true);
            try
            {
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                {
                    var peer = Task.Run(async () =>
                    {
                        await pipe.WaitForConnectionAsync(deadline.Token);
                        string line;
                        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true)) line = await reader.ReadLineAsync();
                        var sent = JObject.Parse(line);
                        Assert.Equal(tool, (string)sent["command"]);
                        Assert.True(JToken.DeepEquals(args, sent["params"]));
                        string corePath = Path.Combine(root, "tests", "Horizun.Core.Tests", "bin", configuration, "net8.0", "Horizun.Core.Tests.dll");
                        var core = Assembly.LoadFrom(corePath);
                        Type docType = core.GetType("Autodesk.Revit.DB.Document", true);
                        object doc = Activator.CreateInstance(docType);
                        docType.GetProperty("Title").SetValue(doc, "Model A");
                        Type uiDocType = core.GetType("Autodesk.Revit.UI.UIDocument", true);
                        object uiDoc = Activator.CreateInstance(uiDocType);
                        uiDocType.GetProperty("Document").SetValue(uiDoc, doc);
                        Type appType = core.GetType("Autodesk.Revit.UI.UIApplication", true);
                        object app = Activator.CreateInstance(appType);
                        appType.GetProperty("ActiveUIDocument").SetValue(app, uiDoc);
                        Type commandType = core.GetType("Horizun.Core.Tests.LargeModelDispatchTests+FakeCommand", true);
                        object command = Activator.CreateInstance(commandType, true);
                        commandType.GetProperty("Name").SetValue(command, tool);
                        Type dispatch = core.GetType("Horizun.Revit.Core.LargeModelDispatch", true);
                        object result = dispatch.GetMethod("Execute", flags).Invoke(null, new[] { app, command, args.ToString(Formatting.None) });
                        Assert.True((bool)result.GetType().GetProperty("Success").GetValue(result));
                        Assert.Equal(1, (int)commandType.GetField("Calls").GetValue(command));
                        var reply = new JObject { ["id"] = sent["id"], ["success"] = true, ["data"] = new JObject { ["ok"] = true } };
                        byte[] bytes = Encoding.UTF8.GetBytes(reply.ToString(Formatting.None) + "\n");
                        await pipe.WriteAsync(bytes, 0, bytes.Length);
                        await pipe.FlushAsync();
                    });
                    pipeClient.GetProperty("DirectoryOverride", flags).SetValue(null, directory);
                    pipeClient.GetProperty("LivenessProbe", flags).SetValue(null, (Func<int, bool>)(_ => true));
                    File.WriteAllText(Path.Combine(directory, "revit-2026-123.json"), new JObject {
                        ["pid"] = 123, ["revit_year"] = "2026", ["pipe_name"] = name, ["auth_token"] = "t"
                    }.ToString());
                    Type program = server.GetType("Horizun.Server.Program", true);
                    var call = program.GetMethod("CallTool", flags, null, new[] { typeof(JObject), typeof(CancellationToken) }, null);
                    var response = (JObject)call.Invoke(null, new object[] { new JObject { ["name"] = tool, ["arguments"] = args }, deadline.Token });
                    Assert.True((bool?)response["isError"] != true, response.ToString());
                    await peer.WaitAsync(deadline.Token);
                }
            }
            finally
            {
                pipeClient.GetProperty("DirectoryOverride", flags).SetValue(null, null);
                pipeClient.GetProperty("LivenessProbe", flags).SetValue(null, null);
                try { Directory.Delete(directory, true); } catch { }
            }
        }
    }
}
