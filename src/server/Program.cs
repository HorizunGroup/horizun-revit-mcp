// Usage:
//   stdio (default):  Bimwright.Rvt.Server.exe              — spawned by Claude/GPT/Cursor
//   HTTP SSE:          Bimwright.Rvt.Server.exe --http 8200  — for Ollama/LM Studio/custom
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Rvt.Plugin; // BimwrightConfig
using Bimwright.Rvt.Server.Bake;
using Bimwright.Rvt.Server.Handlers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Server
{
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Any(a => a == "--help" || a == "-h"))
            {
                PrintHelp();
                return;
            }

            // A9 3-layer config precedence (JSON < env < CLI). AuthToken.Target + transport
            // mode (--http) stay as separate CLI parses for now; A3 toolsets gating uses
            // BimwrightConfig.
            var config = BimwrightConfig.Load(args);
            if (!string.IsNullOrWhiteSpace(config.Target))
            {
                var target = config.Target.ToUpperInvariant();
                if (Array.IndexOf(AuthToken.AllVersions, target) < 0)
                {
                    Console.Error.WriteLine("[Bimwright] Invalid target. Expected: R22|R23|R24|R25|R26|R27");
                    Environment.Exit(1);
                    return;
                }
                AuthToken.Target = target;
            }

            var bakePaths = new BakePaths();
            TryInitializeBakeStorage(bakePaths, out _);

            // Initialize memory system (shared across tool classes + resources)
            var session = new Memory.SessionContext();
            ToolGateway.Session = session;
            ToolGateway.UsageLogger = new UsageEventLogger(bakePaths, config);
            RevitResources.Session = session;

            int httpIndex = Array.IndexOf(args, "--http");
            if (httpIndex >= 0)
            {
                if (httpIndex + 1 >= args.Length || !int.TryParse(args[httpIndex + 1], out var port)
                    || port < 1 || port > 65535)
                {
                    Console.Error.WriteLine("[Bimwright] Invalid --http argument. Expected: --http <port> (1-65535)");
                    Environment.Exit(1);
                    return;
                }
                await RunHttpSse(config, port);
            }
            else
            {
                await RunStdio(config);
            }
        }

        internal static LegacyBakedToolImportResult InitializeBakeStorage(BakePaths paths)
        {
            if (paths == null)
                throw new ArgumentNullException(nameof(paths));

            using var db = new BakeDb(paths);
            db.Migrate();
            var importer = new LegacyBakedToolImporter(paths, db, new ToolBakerAuditLog(paths.AuditJsonl));
            return importer.ImportIfNeeded();
        }

        internal static bool TryInitializeBakeStorage(BakePaths paths, out LegacyBakedToolImportResult result)
        {
            try
            {
                result = InitializeBakeStorage(paths);
                return true;
            }
            catch (Exception ex)
            {
                result = null;
                var pathHint = paths?.Root ?? "(unknown path)";
                Console.Error.WriteLine(
                    $"[Bimwright] Warning: ToolBaker bake storage initialization failed for {pathHint}. " +
                    "The MCP server will continue; baked-tool migration/import can be retried on next startup. " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static async Task RunStdio(BimwrightConfig config)
        {
            var enabled = ToolsetFilter.Resolve(config);
            var builder = Host.CreateApplicationBuilder();
            // stdio MCP stdout must contain JSON-RPC only. ClearProviders() removes default
            // host logging, but the MCP SDK (and any transitive package) may re-add a Console
            // provider that writes to stdout. AddConsole with LogToStandardErrorThreshold=Trace
            // forces every console log line — from any provider re-added downstream — to stderr.
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);
            var mcp = builder.Services
                .AddMcpServer()
                .WithStdioServerTransport();
            mcp = RegisterToolsets(mcp, enabled, config);
            mcp.WithResources<RevitResources>();
            var app = builder.Build();
            await app.RunAsync();
        }

        private static async Task RunHttpSse(BimwrightConfig config, int port)
        {
            var enabled = ToolsetFilter.Resolve(config);
            var builder = WebApplication.CreateBuilder();
            var mcp = builder.Services
                .AddMcpServer()
                .WithHttpTransport();
            mcp = RegisterToolsets(mcp, enabled, config);
            mcp.WithResources<RevitResources>();

            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

            var app = builder.Build();

            app.Use(async (context, next) =>
            {
                var host = context.Request.Host.Host;
                if (host != "127.0.0.1" && host != "localhost")
                {
                    context.Response.StatusCode = 403;
                    context.Response.ContentType = "text/plain";
                    await context.Response.WriteAsync("Forbidden: non-localhost host");
                    return;
                }
                await next();
            });

            app.MapMcp();

            Console.Error.WriteLine($"[Bimwright] SSE server listening on http://127.0.0.1:{port}");
            Console.Error.WriteLine($"[Bimwright] Toolsets enabled: {string.Join(",", enabled.OrderBy(n => n))}");
            await app.RunAsync();
        }

        private static void PrintHelp()
        {
            var usage = string.Join("\n", new[]
            {
                "bimwright — Revit MCP server (bimwright.dev)",
                "",
                "Usage: bimwright [options]",
                "",
                "Transport:",
                "  --http <port>           Run HTTP SSE on 127.0.0.1:<port> (1-65535). Default = stdio.",
                "",
                "Routing:",
                "  --target R22|R23|R24|R25|R26|R27",
                "                          Pin to a specific Revit version (when multiple Revits run).",
                "                          Default: auto-detect via discovery files in %LOCALAPPDATA%\\Bimwright\\.",
                "",
                "Tool exposure (A3 Progressive Disclosure):",
                "  --toolsets <csv>        Comma list of toolsets to enable. Default: query,create,view,schedule,toolbaker,meta,lint.",
                "                          Known toolsets: " + string.Join(", ", ToolsetFilter.KnownToolsets),
                "                          Use 'all' to expose every toolset.",
                "  --read-only             Shortcut that excludes create, modify, and delete toolsets.",
                "",
                "ToolBaker:",
                "  --enable-toolbaker      Allow ToolBaker tools (default ON).",
                "  --disable-toolbaker     Disable ToolBaker tools.",
                "  --enable-adaptive-bake  Enable adaptive ToolBaker suggestions (default OFF).",
                "  --disable-adaptive-bake Disable adaptive ToolBaker suggestions.",
                "  --cache-send-code-bodies",
                "                          Cache send_code_to_revit code bodies locally (default OFF).",
                "  --no-cache-send-code-bodies",
                "                          Disable local send_code_to_revit code body caching.",
                "",
                "Transport security (S7):",
                "  --allow-lan-bind        (plugin-side only — set BIMWRIGHT_ALLOW_LAN_BIND env var in",
                "                          the Revit process environment; server-side flag is documented",
                "                          here for future cross-process propagation.)",
                "",
                "Env vars (override JSON, overridden by CLI):",
                "  BIMWRIGHT_TARGET, BIMWRIGHT_TOOLSETS, BIMWRIGHT_READ_ONLY,",
                "  BIMWRIGHT_ALLOW_LAN_BIND, BIMWRIGHT_ENABLE_TOOLBAKER,",
                "  BIMWRIGHT_ENABLE_ADAPTIVE_BAKE, BIMWRIGHT_CACHE_SEND_CODE_BODIES",
                "",
                "Config file (lowest precedence):",
                "  %LOCALAPPDATA%\\Bimwright\\bimwright.config.json",
                "",
                "Other:",
                "  -h, --help              Show this help and exit.",
            });
            Console.WriteLine(usage);
        }

        private static IMcpServerBuilder RegisterToolsets(IMcpServerBuilder mcp, HashSet<string> enabled, BimwrightConfig config)
        {
            if (enabled.Contains("query"))      mcp = mcp.WithTools<QueryTools>();
            if (enabled.Contains("create"))     mcp = mcp.WithTools<CreateTools>();
            if (enabled.Contains("modify"))     mcp = mcp.WithTools<ModifyTools>();
            if (enabled.Contains("delete"))     mcp = mcp.WithTools<DeleteTools>();
            if (enabled.Contains("view"))       mcp = mcp.WithTools<ViewTools>();
            if (enabled.Contains("schedule"))   mcp = mcp.WithTools<ScheduleTools>();
            if (enabled.Contains("families"))   mcp = mcp.WithTools<FamiliesTools>();
            if (enabled.Contains("graphics"))   mcp = mcp.WithTools<GraphicsTools>();
            if (enabled.Contains("export"))     mcp = mcp.WithTools<ExportTools>();
            if (enabled.Contains("annotation")) mcp = mcp.WithTools<AnnotationTools>();
            if (enabled.Contains("mep"))        mcp = mcp.WithTools<MepTools>();
            if (enabled.Contains("toolbaker"))  mcp = mcp.WithTools<ToolbakerTools>();
            if (enabled.Contains("toolbaker") && config?.EnableAdaptiveBakeOrDefault == true)
                mcp = mcp.WithTools<AdaptiveBakeTools>();
            if (enabled.Contains("meta"))       mcp = mcp.WithTools<MetaTools>();
            if (enabled.Contains("lint"))       mcp = mcp.WithTools<LintTools>();
            return mcp;
        }

        private static Type[] ResolveRegisteredToolTypes(HashSet<string> enabled, BimwrightConfig config)
        {
            var types = new List<Type>();
            if (enabled.Contains("query"))      types.Add(typeof(QueryTools));
            if (enabled.Contains("create"))     types.Add(typeof(CreateTools));
            if (enabled.Contains("modify"))     types.Add(typeof(ModifyTools));
            if (enabled.Contains("delete"))     types.Add(typeof(DeleteTools));
            if (enabled.Contains("view"))       types.Add(typeof(ViewTools));
            if (enabled.Contains("schedule"))   types.Add(typeof(ScheduleTools));
            if (enabled.Contains("families"))   types.Add(typeof(FamiliesTools));
            if (enabled.Contains("graphics"))   types.Add(typeof(GraphicsTools));
            if (enabled.Contains("export"))     types.Add(typeof(ExportTools));
            if (enabled.Contains("annotation")) types.Add(typeof(AnnotationTools));
            if (enabled.Contains("mep"))        types.Add(typeof(MepTools));
            if (enabled.Contains("toolbaker"))  types.Add(typeof(ToolbakerTools));
            if (enabled.Contains("toolbaker") && config?.EnableAdaptiveBakeOrDefault == true)
                types.Add(typeof(AdaptiveBakeTools));
            if (enabled.Contains("meta"))       types.Add(typeof(MetaTools));
            if (enabled.Contains("lint"))       types.Add(typeof(LintTools));
            return types.ToArray();
        }
    }

    /// <summary>
    /// Shared plugin-connection plumbing used by every toolset class. Owns the socket/
    /// pipe lifecycle, response read loop, pending-request correlation, and session
    /// call recording. Toolset classes contain only the MCP tool-method shells.
    /// </summary>
    internal static class ToolGateway
    {
        public static Memory.SessionContext Session { get; set; }
        public static UsageEventLogger UsageLogger { get; set; }
        public static string CurrentRevitVersion { get; private set; }

        private static TcpClient _client;
        private static NamedPipeClientStream _pipeStream;
        private static StreamReader _reader;
        private static StreamWriter _writer;
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new ConcurrentDictionary<string, TaskCompletionSource<string>>();
        private static readonly object _connectLock = new object();
        private static readonly JsonSerializerSettings RequestJsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };
        private static volatile bool _connected;
        private static string _token;

        private static void EnsureConnected()
        {
            if (_connected && (_client?.Connected == true || _pipeStream?.IsConnected == true))
                return;

            lock (_connectLock)
            {
                if (_connected && (_client?.Connected == true || _pipeStream?.IsConnected == true))
                    return;

                _connected = false;
                try { _client?.Close(); } catch { }
                try { _pipeStream?.Close(); } catch { }
                _client = null;
                _pipeStream = null;

                Stream stream = null;

                var target = AuthToken.Target; // null = auto, "R22"-"R27" = specific version

                // Try Named Pipe first (R25-R27).
                // If the discovery file exists but the connect itself fails (plugin unloaded
                // while Revit stayed alive, or some transient state), fall through to TCP
                // rather than giving up the whole connection attempt.
                if (AuthToken.TryReadPipe(out var pipeName, out var pipeToken, out var pipeVer))
                {
                    try
                    {
                        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                            PipeOptions.Asynchronous);
                        pipe.Connect(5000);
                        _token = pipeToken;
                        CurrentRevitVersion = pipeVer;
                        _pipeStream = pipe;
                        stream = pipe;
                        Console.Error.WriteLine($"[Bimwright] Connected to Revit {pipeVer} via Named Pipe: {pipeName}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[Bimwright] Pipe connect failed ({pipeVer}: {ex.Message}) — falling back to TCP");
                        try { _pipeStream?.Close(); } catch { }
                        _pipeStream = null;
                    }
                }

                // Fall back to TCP (R22-R24) if pipe did not connect.
                if (stream == null && AuthToken.TryReadTcp(out var port, out var tcpToken, out var tcpVer))
                {
                    _token = tcpToken;
                    CurrentRevitVersion = tcpVer;
                    _client = new TcpClient();
                    _client.Connect("127.0.0.1", port);
                    stream = _client.GetStream();
                    Console.Error.WriteLine($"[Bimwright] Connected to Revit {tcpVer} via TCP on port {port}");
                }

                if (stream == null)
                {
                    var which = target != null ? $"(target={target})" : "(auto-detect R22-R27)";
                    throw new InvalidOperationException(
                        $"Revit MCP plugin not running {which}. Check discovery files in %LOCALAPPDATA%\\Bimwright\\");
                }

                _reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _connected = true;

                var readThread = new Thread(ReadLoop) { IsBackground = true, Name = "Bimwright.ResponseReader" };
                readThread.Start();
            }
        }

        private static void ReadLoop()
        {
            try
            {
                while (_connected)
                {
                    var line = _reader?.ReadLine();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var obj = JObject.Parse(line);
                        var id = obj.Value<string>("id");
                        if (id != null && _pending.TryRemove(id, out var tcs))
                        {
                            tcs.TrySetResult(line);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                _connected = false;
            }
        }

        /// <summary>
        /// Close the current Server↔Plugin connection and set a new target version.
        /// Next <see cref="SendToRevit"/> call will reconnect against the new target.
        /// Pass <c>null</c> to clear the pin and re-enable auto-detect.
        /// Cancels any in-flight requests — they'd be routed to the now-dead connection.
        /// </summary>
        public static void Reconnect(string newTarget)
        {
            lock (_connectLock)
            {
                _connected = false;
                try { _client?.Close(); } catch { }
                try { _pipeStream?.Close(); } catch { }
                _client = null;
                _pipeStream = null;
                _reader = null;
                _writer = null;
                _token = null;
                CurrentRevitVersion = null;
                foreach (var kv in _pending)
                {
                    kv.Value.TrySetException(new OperationCanceledException(
                        "switch_target initiated — in-flight request cancelled."));
                }
                _pending.Clear();
                AuthToken.Target = newTarget;
            }
        }

        public static async Task<JObject> SendToRevit(string command, object parameters = null)
        {
            EnsureConnected();

            var id = $"req-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            var request = JsonConvert.SerializeObject(new { id, command, @params = parameters ?? new { }, token = _token }, RequestJsonSettings);

            var tcs = new TaskCompletionSource<string>();
            _pending[id] = tcs;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _writer.WriteLine(request);

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _pending.TryRemove(id, out _);
                sw.Stop();
                var paramsStr = parameters != null ? JsonConvert.SerializeObject(parameters, RequestJsonSettings) : null;
                Session?.RecordCall(command, paramsStr, false, sw.ElapsedMilliseconds, "Timeout (60s)");
                UsageLogger?.RecordToolCall(command, paramsStr, false);
                throw new TimeoutException("Request timed out (60s). Revit may be in a modal dialog.");
            }

            sw.Stop();
            var responseLine = await tcs.Task;
            var response = JObject.Parse(responseLine);
            var paramsJson = parameters != null ? JsonConvert.SerializeObject(parameters, RequestJsonSettings) : null;

            if (response.Value<bool>("success"))
            {
                var data = response["data"] as JObject ?? new JObject();
                Session?.RecordCall(command, paramsJson, true, sw.ElapsedMilliseconds,
                    resultJson: data.ToString(Formatting.None));
                UsageLogger?.RecordToolCall(command, paramsJson, true);
                return data;
            }
            else
            {
                var error = response.Value<string>("error") ?? "Unknown error from Revit";
                Session?.RecordCall(command, paramsJson, false, sw.ElapsedMilliseconds, error);
                UsageLogger?.RecordToolCall(command, paramsJson, false);
                throw new InvalidOperationException(error);
            }
        }
    }

    // =====================================================================
    // Toolset classes — one per aspect #3 §A3 group. Registration happens in
    // Program.RegisterToolsets() driven by config.Toolsets. Each method wraps
    // ToolGateway.SendToRevit with a catch-all that surfaces the error to the
    // MCP client as plain text instead of throwing.
    // =====================================================================

    [McpServerToolType, Toolset("query")]
    public class QueryTools
    {
        [McpServerTool(Name = "get_current_view_info", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Get active view info. Returns viewName, viewType (FloorPlan/Section/3D/Sheet), level, scale, detailLevel, displayStyle. Call before creating elements to know active level.")]
        public static async Task<string> GetCurrentViewInfo()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_current_view_info");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_selected_elements", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Get currently selected Revit elements. Returns array of {id, name, category, typeName}. Call before operating on user selection (color, delete, move).")]
        public static async Task<string> GetSelectedElements()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_selected_elements");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_available_family_types", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List loadable family types. Returns {familyName, typeName, typeId} grouped by category. Optional: filter by category (e.g. 'Walls', 'Doors', 'Pipes' — NOT 'OST_Walls'). Feed typeId into create_point_based_element.")]
        public static async Task<string> GetAvailableFamilyTypes(string category = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_available_family_types", new { category });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "ai_element_filter", Destructive = false, Idempotent = true), System.ComponentModel.Description("Filter elements by category + parameter. Numeric values in mm (auto-converted). category uses human name ('Pipes', NOT 'OST_Pipes'). Operators: equals/contains/startswith/greaterthan/lessthan. select=true highlights results. Example: category='Pipes', parameterName='Diameter', parameterValue='200', operator='greaterthan', select=true.")]
        public static async Task<string> AiElementFilter(string category, string parameterName = "", string parameterValue = "", string @operator = "equals", int limit = 100, bool select = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("ai_element_filter", new { category, parameterName, parameterValue, @operator, limit, select });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "analyze_model_statistics", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Count elements grouped by category (Walls, Doors, Pipes, etc.). Call to understand project scope before detailed queries.")]
        public static async Task<string> AnalyzeModelStatistics()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("analyze_model_statistics");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_material_quantities", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Sum material quantities (area m², volume m³) by category. Required: category — human name ('Walls', 'Floors' — NOT 'OST_Walls').")]
        public static async Task<string> GetMaterialQuantities(string category)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_material_quantities", new { category });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_element_details", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read detailed metadata for one or more elements. Returns identity, category, type, level, workset, phase, owner view, design option, group/assembly ids, location, and bounding box in mm.")]
        public static async Task<string> GetElementDetails(long[] elementIds)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_element_details", new { elementIds });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_element_parameters", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read instance parameters for one or more elements. Returns storage type, read-only state, display value, raw value, and data/spec ids.")]
        public static async Task<string> GetElementParameters(long[] elementIds, bool includeReadOnly = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_element_parameters", new { elementIds, includeReadOnly });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_type_parameters", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read type parameters from explicit type ids or from the types of provided element ids.")]
        public static async Task<string> GetTypeParameters(long[] elementIds = null, long[] typeIds = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_type_parameters", new { elementIds, typeIds });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "list_project_parameters", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List project/shared parameter bindings, including instance/type binding kind and bound categories.")]
        public static async Task<string> ListProjectParameters(bool includeCategories = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_project_parameters", new { includeCategories });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_element_relationships", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read host, group, assembly, owner view, design option, family nesting, and dependent-element relationships for elements.")]
        public static async Task<string> GetElementRelationships(long[] elementIds, bool includeDependents = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_element_relationships", new { elementIds, includeDependents });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "list_groups", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List model/detail/attached groups with type, owner view, parent, and optional member ids.")]
        public static async Task<string> ListGroups(string groupKind = "all", bool includeMembers = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_groups", new { groupKind, includeMembers });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_group_members", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read a group instance and its member elements with category, type, owner view, and pinned state.")]
        public static async Task<string> GetGroupMembers(long groupId)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_group_members", new { groupId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "list_assemblies", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List assembly instances with type, naming category, member count, and optional member ids.")]
        public static async Task<string> ListAssemblies(bool includeMembers = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_assemblies", new { includeMembers });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_assembly_members", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read an assembly instance and its member elements with category, type, group, and workset ids.")]
        public static async Task<string> GetAssemblyMembers(long assemblyId)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_assembly_members", new { assemblyId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "list_worksets", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List document worksets and active workset. Optionally includes per-workset element counts.")]
        public static async Task<string> ListWorksets(bool includeElementCounts = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_worksets", new { includeElementCounts });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("schedule")]
    public class ScheduleTools
    {
        [McpServerTool(Name = "list_schedules", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List all schedules in the project. Optional filters: categoryFilter (case-insensitive substring on resolved category name), namePattern (case-insensitive substring on schedule name).")]
        public static async Task<string> ListSchedules(string categoryFilter = "", string namePattern = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_schedules", new { categoryFilter, namePattern });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_schedule_definition", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Get the full structural definition of a schedule: fields (parameter/formula/combined), filters, sort/group, and settings. Identify schedule by `scheduleId` (long) or `scheduleName`.")]
        public static async Task<string> GetScheduleDefinition(long? scheduleId = null, string scheduleName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_schedule_definition", new { scheduleId, scheduleName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_schedule_data", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Get the rendered tabular content of a schedule (header row + body rows) with pagination. Optional cell metadata (cell type + merged cells).")]
        public static async Task<string> GetScheduleData(long? scheduleId = null, string scheduleName = "", int startRow = 0, int maxRows = 200, bool includeCellMeta = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_schedule_data", new { scheduleId, scheduleName, startRow, maxRows, includeCellMeta });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_schedule_formulas", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Extract all calculated (formula) and combined-parameter fields from a schedule, with parsed formula dependencies. Useful for auditing, debugging, or copying formulas between schedules.")]
        public static async Task<string> GetScheduleFormulas(long? scheduleId = null, string scheduleName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_schedule_formulas", new { scheduleId, scheduleName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_schedulable_fields", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List parameters that CAN be added as fields to a schedule but have not been added yet. Pre-step for add_schedule_field — call this to discover valid parameter names.")]
        public static async Task<string> GetSchedulableFields(long? scheduleId = null, string scheduleName = "", string[] kindFilter = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_schedulable_fields", new { scheduleId, scheduleName, kindFilter });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "find_schedule_elements", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Find Revit elements aggregated by a schedule (using FilteredElementCollector scoped to the schedule's id). Returns count grouped by category and per-element {id, name, category, typeName}. Optional includeParameters returns each element's visible parameters with unit-corrected values.")]
        public static async Task<string> FindScheduleElements(long? scheduleId = null, string scheduleName = "", bool groupByCategory = true, bool includeParameters = false, int limit = 500)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("find_schedule_elements", new { scheduleId, scheduleName, groupByCategory, includeParameters, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_schedule", Destructive = false), System.ComponentModel.Description("Create a new schedule from a declarative spec. Supports three field kinds in one transaction: parameter (existing Revit param), formula (calculated value field), and combined (concatenated parameters with separators). Optional filters, sort/group, and isItemized.")]
        public static async Task<string> CreateSchedule(string category, string name, string fields, string filters = null, string sortGroup = null, bool isItemized = true)
        {
            try
            {
                var parsedFields = JArray.Parse(fields);
                var parsedFilters = string.IsNullOrWhiteSpace(filters) ? null : JArray.Parse(filters);
                var parsedSortGroup = string.IsNullOrWhiteSpace(sortGroup) ? null : JArray.Parse(sortGroup);
                var result = await ToolGateway.SendToRevit("create_schedule", new { category, name, fields = parsedFields, filters = parsedFilters, sortGroup = parsedSortGroup, isItemized });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "add_schedule_field", Destructive = false), System.ComponentModel.Description("Add one new field to an existing schedule. Supports parameter, formula, or combined-parameter kinds via a discriminated-union spec. Optional insertIndex, columnHeading, hidden, columnWidth (mm).")]
        public static async Task<string> AddScheduleField(string field, long? scheduleId = null, string scheduleName = "", int? insertIndex = null, string columnHeading = "", bool hidden = false, double? columnWidth = null)
        {
            try
            {
                var parsedField = JObject.Parse(field);
                var result = await ToolGateway.SendToRevit("add_schedule_field", new { scheduleId, scheduleName, field = parsedField, insertIndex, columnHeading, hidden, columnWidth });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "update_schedule_field", Destructive = false), System.ComponentModel.Description("Modify an existing schedule field's properties: columnHeading, hidden, columnWidth, horizontalAlignment, headingOrientation, formula (only if calculated), combinedParameters (only if combined), isTotal, isPercentage, displayType. Cannot change the underlying parameter of a parameter field — use remove + add instead.")]
        public static async Task<string> UpdateScheduleField(string fieldRef, string changes, long? scheduleId = null, string scheduleName = "")
        {
            try
            {
                var parsedFieldRef = JObject.Parse(fieldRef);
                var parsedChanges = JObject.Parse(changes);
                var result = await ToolGateway.SendToRevit("update_schedule_field", new { scheduleId, scheduleName, fieldRef = parsedFieldRef, changes = parsedChanges });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "apply_schedule_filter_sort", Destructive = false), System.ComponentModel.Description("Partially update a schedule's filters, sort/group, and settings. filters/sortGroup replace only when supplied; omitted sections are preserved.")]
        public static async Task<string> ApplyScheduleFilterSort(long? scheduleId = null, string scheduleName = "", string filters = null, string sortGroup = null, string settings = null)
        {
            try
            {
                var parsedFilters = string.IsNullOrWhiteSpace(filters) ? null : JArray.Parse(filters);
                var parsedSortGroup = string.IsNullOrWhiteSpace(sortGroup) ? null : JArray.Parse(sortGroup);
                var parsedSettings = string.IsNullOrWhiteSpace(settings) ? null : JObject.Parse(settings);
                var result = await ToolGateway.SendToRevit("apply_schedule_filter_sort", new { scheduleId, scheduleName, filters = parsedFilters, sortGroup = parsedSortGroup, settings = parsedSettings });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("families")]
    public class FamiliesTools
    {
        [McpServerTool(Name = "list_loaded_families", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List all loaded families (loadable + in-place + system) in the active document grouped by category. Returns id, name, category, kind (system|loadable|inplace), type_count, optional instance_count, and is_editable. Filter via categoryFilter (case-insensitive substring) and kindFilter (all|system|loadable|inplace).")]
        public static async Task<string> ListLoadedFamilies(string categoryFilter = "", string kindFilter = "all", bool includeInstanceCount = false, int limit = 1000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_loaded_families", new { category_filter = categoryFilter, kind_filter = kindFilter, include_instance_count = includeInstanceCount, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "load_family_from_path", Destructive = false), System.ComponentModel.Description("Load an .rfa family file from disk into the active document. Returns loaded family id and the new symbol/type ids. overwriteExisting controls IFamilyLoadOptions.OnFamilyFound; overwriteParameterValues forwards to the same callback.")]
        public static async Task<string> LoadFamilyFromPath(string path, bool overwriteExisting = true, bool overwriteParameterValues = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("load_family_from_path", new { path, overwrite_existing = overwriteExisting, overwrite_parameter_values = overwriteParameterValues });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "unload_family", Destructive = true), System.ComponentModel.Description("Remove (purge) a loadable family from the document. Identify by familyId or familyName. cascadeDeleteInstances=true to also delete placed instances; otherwise error if instances exist. dryRun=true returns the projected effect without changing the model. System families cannot be unloaded.")]
        public static async Task<string> UnloadFamily(long? familyId = null, string familyName = "", bool cascadeDeleteInstances = false, bool dryRun = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("unload_family", new { family_id = familyId, family_name = familyName, cascade_delete_instances = cascadeDeleteInstances, dry_run = dryRun });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "duplicate_family_type", Destructive = false), System.ComponentModel.Description("Duplicate a FamilySymbol or system type within its family under newTypeName, optionally setting type parameter overrides (JSON object as string, parameter name → value). Returns the new type id. Works for FamilySymbol and ElementType subclasses (WallType, FloorType, etc.).")]
        public static async Task<string> DuplicateFamilyType(long sourceTypeId, string newTypeName, string typeParameterOverrides = "")
        {
            try
            {
                var parsedOverrides = string.IsNullOrWhiteSpace(typeParameterOverrides) ? null : JObject.Parse(typeParameterOverrides);
                var result = await ToolGateway.SendToRevit("duplicate_family_type", new { source_type_id = sourceTypeId, new_type_name = newTypeName, type_parameter_overrides = parsedOverrides });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "rename_family_type", Destructive = false), System.ComponentModel.Description("Rename a FamilySymbol or system type. Must be unique within the family. Catches Autodesk.Revit.Exceptions.ArgumentException for duplicate/invalid names and returns a clean error DTO without throwing.")]
        public static async Task<string> RenameFamilyType(long typeId, string newName)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("rename_family_type", new { type_id = typeId, new_name = newName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "audit_families", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read-only audit of loaded families. Detects unused families (zero instances), in-place families, duplicate names, and high type-count families. Returns recommendations. Tunable include flags + highTypeCountThreshold.")]
        public static async Task<string> AuditFamilies(bool includeUnused = true, bool includeInplace = true, bool includeDuplicateNames = true, bool includeHighTypeCount = true, int highTypeCountThreshold = 20)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("audit_families", new { include_unused = includeUnused, include_inplace = includeInplace, include_duplicate_names = includeDuplicateNames, include_high_type_count = includeHighTypeCount, high_type_count_threshold = highTypeCountThreshold });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "replace_family_type", Destructive = false), System.ComponentModel.Description("Replace all instances of FamilySymbol A with FamilySymbol B across the project, active view, or selection. Both types must be the same category. dryRun=true previews counts without changing the model. Target symbol is auto-activated.")]
        public static async Task<string> ReplaceFamilyType(long fromTypeId, long toTypeId, string scope = "all", long? viewId = null, bool dryRun = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("replace_family_type", new { from_type_id = fromTypeId, to_type_id = toTypeId, scope, view_id = viewId, dry_run = dryRun });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_family_instances", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List placed instances of a Family (or a specific type within it) with location/host/level DTOs in mm. viewOnly=true restricts to the active view. Returns location_kind (point|line|null), coordinates in mm, host_id/name, mark.")]
        public static async Task<string> GetFamilyInstances(long? familyId = null, string familyName = "", string typeName = "", bool viewOnly = false, int limit = 1000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_family_instances", new { family_id = familyId, family_name = familyName, type_name = typeName, view_only = viewOnly, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "list_family_types_in_family", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Deep listing of all types within ONE family, including each type's parameter values (unit-converted to mm/m²/m³/deg). includeBuiltInOnly=true filters out shared/project params. Returns is_active per type. More detailed than get_available_family_types.")]
        public static async Task<string> ListFamilyTypesInFamily(long? familyId = null, string familyName = "", bool includeParameterValues = true, bool includeBuiltInOnly = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_family_types_in_family", new { family_id = familyId, family_name = familyName, include_parameter_values = includeParameterValues, include_built_in_only = includeBuiltInOnly });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "export_family_to_path", Destructive = false), System.ComponentModel.Description("Save a loadable family from the current project back to an .rfa file at outputPath. Writes to disk (not ReadOnly). Rejects in-place and system families. overwriteExisting=false errors if the file already exists. Uses doc.EditFamily + Document.SaveAs.")]
        public static async Task<string> ExportFamilyToPath(string outputPath, long? familyId = null, string familyName = "", bool overwriteExisting = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_family_to_path", new { family_id = familyId, family_name = familyName, output_path = outputPath, overwrite_existing = overwriteExisting });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("create")]
    public class CreateTools
    {
        [McpServerTool(Name = "create_line_based_element", Destructive = false), System.ComponentModel.Description("Create a line-based element (wall). Params: elementType, startX/Y, endX/Y (mm), level (name), typeId (optional), height (mm, default 3000).")]
        public static async Task<string> CreateLineBasedElement(string elementType, double startX, double startY, double endX, double endY, string level = "", long? typeId = null, double height = 3000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_line_based_element", new { elementType, startX, startY, endX, endY, level, typeId, height });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_point_based_element", Destructive = false), System.ComponentModel.Description("Create a point-based element (door, window, furniture). Params: typeId (from get_available_family_types), x/y/z (mm), level (name).")]
        public static async Task<string> CreatePointBasedElement(long typeId, double x, double y, double z = 0, string level = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_point_based_element", new { typeId, x, y, z, level });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_surface_based_element", Destructive = false), System.ComponentModel.Description("Create a surface-based element (floor, ceiling). Params: elementType, points (JSON array of {x,y} in mm, min 3), level (name), typeId (optional). Example points: [{\"x\":0,\"y\":0},{\"x\":6000,\"y\":0},{\"x\":6000,\"y\":4000},{\"x\":0,\"y\":4000}].")]
        public static async Task<string> CreateSurfaceBasedElement(string elementType, string points, string level = "", long? typeId = null)
        {
            try
            {
                var parsedPoints = JArray.Parse(points);
                var result = await ToolGateway.SendToRevit("create_surface_based_element", new { elementType, points = parsedPoints, level, typeId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_level", Destructive = false), System.ComponentModel.Description("Create a level at specified elevation. Params: elevation (mm), name (optional).")]
        public static async Task<string> CreateLevel(double elevation, string name = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_level", new { elevation, name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_grid", Destructive = false), System.ComponentModel.Description("Create a grid line. Params: startX/Y, endX/Y (mm), name (optional).")]
        public static async Task<string> CreateGrid(double startX, double startY, double endX, double endY, string name = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_grid", new { startX, startY, endX, endY, name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_room", Destructive = false), System.ComponentModel.Description("Create and place a room. Params: x/y (mm), level (name), name (optional), number (optional).")]
        public static async Task<string> CreateRoom(double x, double y, string level = "", string name = "", string number = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_room", new { x, y, level, name, number });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_group_from_elements", Destructive = false), System.ComponentModel.Description("Create a Revit group from two or more element ids. Optional name renames the generated group type.")]
        public static async Task<string> CreateGroupFromElements(long[] elementIds, string name = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_group_from_elements", new { elementIds, name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("modify")]
    public class ModifyTools
    {
        [McpServerTool(Name = "operate_element", Destructive = false), System.ComponentModel.Description("Select/hide/isolate/color elements in current view. operation: select (highlight), hide, unhide, isolate (hide everything else), setcolor (RGB override). elementIds: JSON int array e.g. '[12345, 67890]'. For setcolor: r/g/b 0-255 (default red 255,0,0).")]
        public static async Task<string> OperateElement(string operation, string elementIds, byte r = 255, byte g = 0, byte b = 0)
        {
            try
            {
                var parsedIds = JArray.Parse(elementIds).ToObject<long[]>();
                var result = await ToolGateway.SendToRevit("operate_element", new { operation, elementIds = parsedIds, r, g, b });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "color_elements", Destructive = false, Idempotent = true), System.ComponentModel.Description("Color-code elements by parameter value in current view. Auto-assigns distinct colors per unique value. category uses human name ('Walls', NOT 'OST_Walls'). Example: category='Pipes', parameterName='System Type' → each system type gets a different color.")]
        public static async Task<string> ColorElements(string category, string parameterName)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("color_elements", new { category, parameterName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "set_element_parameter_values", Destructive = false), System.ComponentModel.Description("Set an instance parameter on multiple elements. valueType can be auto/string/integer/double/elementId; length-like doubles use mm input.")]
        public static async Task<string> SetElementParameterValues(long[] elementIds, string parameterName, string value, string valueType = "auto")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_element_parameter_values", new { elementIds, parameterName, value, valueType });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "set_type_parameter_values", Destructive = false), System.ComponentModel.Description("Set a type parameter on explicit type ids or on the types resolved from element ids.")]
        public static async Task<string> SetTypeParameterValues(string parameterName, string value, long[] typeIds = null, long[] elementIds = null, string valueType = "auto")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_type_parameter_values", new { parameterName, value, typeIds, elementIds, valueType });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "change_element_type", Destructive = false), System.ComponentModel.Description("Change one or more elements to a target ElementType id after validating type compatibility.")]
        public static async Task<string> ChangeElementType(long[] elementIds, long typeId)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("change_element_type", new { elementIds, typeId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "assign_elements_to_workset", Destructive = false), System.ComponentModel.Description("Assign elements to a user workset by worksetId or worksetName in a workshared document.")]
        public static async Task<string> AssignElementsToWorkset(long[] elementIds, long? worksetId = null, string worksetName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("assign_elements_to_workset", new { elementIds, worksetId, worksetName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("delete")]
    public class DeleteTools
    {
        [McpServerTool(Name = "delete_element", Idempotent = true), System.ComponentModel.Description("Delete elements by ID. DESTRUCTIVE — cannot be undone via MCP. elementIds: JSON int array e.g. '[12345, 67890]'. Fetch IDs from get_selected_elements or ai_element_filter first.")]
        public static async Task<string> DeleteElement(string elementIds)
        {
            try
            {
                var parsedIds = JArray.Parse(elementIds).ToObject<long[]>();
                var result = await ToolGateway.SendToRevit("delete_element", new { elementIds = parsedIds });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("view")]
    public class ViewTools
    {
        [McpServerTool(Name = "create_view", Destructive = false), System.ComponentModel.Description("Create a view (floorplan or 3d). Params: viewType ('floorplan' or '3d'), level (name, required for floorplan), name (optional).")]
        public static async Task<string> CreateView(string viewType, string level = "", string name = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_view", new { viewType, level, name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "place_view_on_sheet", Destructive = false), System.ComponentModel.Description("Place a view on a sheet. Auto-creates sheet if sheetId omitted. Params: viewId (required), sheetId (optional), sheetNumber (optional), sheetName (optional).")]
        public static async Task<string> PlaceViewOnSheet(long viewId, long? sheetId = null, string sheetNumber = "", string sheetName = "MCP Generated Sheet")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("place_view_on_sheet", new { viewId, sheetId, sheetNumber, sheetName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "analyze_sheet_layout", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Analyze a sheet's title block + viewport layout in mm. Provide sheetNumber (e.g. 'ISO-005') or sheetId; if neither, uses active view when it is a sheet. Returns title block size, viewport centers, widths, heights, scales.")]
        public static async Task<string> AnalyzeSheetLayout(string sheetNumber = "", long? sheetId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("analyze_sheet_layout", new { sheetNumber, sheetId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("export")]
    public class ExportTools
    {
        [McpServerTool(Name = "export_room_data", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Export all rooms. Returns array of {name, number, area (m²), perimeter, level, department, volume (m³)}. For space analysis and reporting.")]
        public static async Task<string> ExportRoomData()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_room_data");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "export_pdf", Destructive = false), System.ComponentModel.Description("Export sheets or views to PDF. outputFolder must be an existing absolute path. viewIds defaults to the active view. combine=true produces one combined PDF.")]
        public static async Task<string> ExportPdf(string outputFolder, long[] viewIds = null, bool combine = false, string fileName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_pdf", new { output_folder = outputFolder, view_ids = viewIds, combine, file_name = fileName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "export_dwg", Destructive = false), System.ComponentModel.Description("Export sheets or views to AutoCAD DWG. outputFolder must be an existing absolute path. viewIds defaults to the active view. settingsName optionally selects a saved ExportDWGSettings.")]
        public static async Task<string> ExportDwg(string outputFolder, long[] viewIds = null, string settingsName = "", string fileNamePrefix = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_dwg", new { output_folder = outputFolder, view_ids = viewIds, settings_name = settingsName, file_name_prefix = fileNamePrefix });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "export_dgn", Destructive = false), System.ComponentModel.Description("Export sheets or views to MicroStation DGN. outputFolder must be an existing absolute path. viewIds defaults to the active view.")]
        public static async Task<string> ExportDgn(string outputFolder, long[] viewIds = null, string fileNamePrefix = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_dgn", new { output_folder = outputFolder, view_ids = viewIds, file_name_prefix = fileNamePrefix });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "export_dwf", Destructive = false), System.ComponentModel.Description("Export sheets or views to Autodesk DWF/DWFx. outputFolder must be an existing absolute path. viewIds defaults to the active view. useDwfx=true exports DWFx.")]
        public static async Task<string> ExportDwf(string outputFolder, long[] viewIds = null, string fileName = "", bool useDwfx = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_dwf", new { output_folder = outputFolder, view_ids = viewIds, file_name = fileName, use_dwfx = useDwfx });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "export_ifc", Destructive = false), System.ComponentModel.Description("Export the model to IFC. outputFolder must be an existing absolute path. ifcVersion: IFC2x3|IFC4|default.")]
        public static async Task<string> ExportIfc(string outputFolder, string fileName, string ifcVersion = "default")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_ifc", new { output_folder = outputFolder, file_name = fileName, ifc_version = ifcVersion });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "export_nwc", Destructive = false), System.ComponentModel.Description("Export the model to Navisworks NWC. outputFolder must be an existing absolute path. Optional exportScopeViewId scopes the export to one view. Requires the Navisworks NWC exporter add-in installed.")]
        public static async Task<string> ExportNwc(string outputFolder, string fileName, long? exportScopeViewId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_nwc", new { output_folder = outputFolder, file_name = fileName, export_scope_view_id = exportScopeViewId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "export_fbx", Destructive = false), System.ComponentModel.Description("Export a 3D view to Autodesk FBX. outputFolder must be an existing absolute path. viewId must reference a 3D view (defaults to the active view, which must be 3D).")]
        public static async Task<string> ExportFbx(string outputFolder, string fileName, long? viewId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_fbx", new { output_folder = outputFolder, file_name = fileName, view_id = viewId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "export_gbxml", Destructive = false), System.ComponentModel.Description("Export the model's energy analytical data to gbXML. outputFolder must be an existing absolute path. Requires rooms/spaces with energy settings.")]
        public static async Task<string> ExportGbxml(string outputFolder, string fileName)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_gbxml", new { output_folder = outputFolder, file_name = fileName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "export_image", Destructive = false), System.ComponentModel.Description("Export a view to a raster image. outputPath is an absolute file path (.png/.jpg). viewId defaults to the active view. pixelSize sets the longer dimension. imageFormat: png|jpeg.")]
        public static async Task<string> ExportImage(string outputPath, long? viewId = null, int pixelSize = 2048, string imageFormat = "png")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_image", new { output_path = outputPath, view_id = viewId, pixel_size = pixelSize, image_format = imageFormat });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "export_schedule_csv", Destructive = false), System.ComponentModel.Description("Export a Revit schedule's data to a delimited text/CSV file. outputPath is an absolute file path. Identify the schedule by scheduleId or scheduleName.")]
        public static async Task<string> ExportScheduleCsv(string outputPath, long? scheduleId = null, string scheduleName = "", string delimiter = ",")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_schedule_csv", new { output_path = outputPath, schedule_id = scheduleId, schedule_name = scheduleName, delimiter });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "export_elements_data", Destructive = false), System.ComponentModel.Description("Export element parameter data for a category to a JSON or CSV file. outputPath is an absolute file path. parameterNames defaults to a common set. format: json|csv.")]
        public static async Task<string> ExportElementsData(string category, string outputPath, string[] parameterNames = null, string format = "json")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_elements_data", new { category, output_path = outputPath, parameter_names = parameterNames, format });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "batch_export_sheets", Destructive = false), System.ComponentModel.Description("Export many sheets at once to PDF or DWG. outputFolder must be an existing absolute path. format: pdf|dwg. sheetIds defaults to ALL sheets; sheetNumberFilter narrows by sheet-number substring.")]
        public static async Task<string> BatchExportSheets(string outputFolder, string format, long[] sheetIds = null, string sheetNumberFilter = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("batch_export_sheets", new { output_folder = outputFolder, format, sheet_ids = sheetIds, sheet_number_filter = sheetNumberFilter });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "list_export_settings", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List saved export/print configurations: DWG export setups, named print settings, and view/sheet sets.")]
        public static async Task<string> ListExportSettings()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_export_settings", new { });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_view_sheet_set", Destructive = false), System.ComponentModel.Description("Create a named ViewSheetSet (a saved set of views/sheets) for batch printing/exporting. viewIds are the ViewSheet/View ElementIds to include.")]
        public static async Task<string> CreateViewSheetSet(string name, long[] viewIds)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_view_sheet_set", new { name, view_ids = viewIds });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_print_settings", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Report the document's PrintManager state and all named print settings + view/sheet sets.")]
        public static async Task<string> GetPrintSettings()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_print_settings", new { });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("annotation")]
    public class AnnotationTools
    {
        [McpServerTool(Name = "tag_all_walls", Destructive = false, Idempotent = true), System.ComponentModel.Description("Tag all walls in current view at midpoint. Skips already-tagged walls. Returns count of new tags.")]
        public static async Task<string> TagAllWalls()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("tag_all_walls");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "tag_all_rooms", Destructive = false, Idempotent = true), System.ComponentModel.Description("Tag all rooms in current view at location point. Skips already-tagged rooms. Returns count of new tags.")]
        public static async Task<string> TagAllRooms()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("tag_all_rooms");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("mep")]
    public class MepTools
    {
        [McpServerTool(Name = "detect_system_elements", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Walk an MEP system from a seed element. Traverses connectors to find all pipes, fittings, accessories, equipment in the same system. Returns IDs grouped by category + bounding box in mm. Fetch seed via get_selected_elements.")]
        public static async Task<string> DetectSystemElements(long elementId)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("detect_system_elements", new { elementId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_duct", Destructive = false), System.ComponentModel.Description("Create an HVAC duct between two points (mm). ductTypeId/systemTypeId/levelId default to first available / nearest level. Provide diameter for round duct OR width+height (mm) for rectangular.")]
        public static async Task<string> CreateDuct(double startX, double startY, double startZ, double endX, double endY, double endZ, long? ductTypeId = null, long? systemTypeId = null, long? levelId = null, double? width = null, double? height = null, double? diameter = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_duct", new { start_x = startX, start_y = startY, start_z = startZ, end_x = endX, end_y = endY, end_z = endZ, duct_type_id = ductTypeId, system_type_id = systemTypeId, level_id = levelId, width, height, diameter });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_pipe", Destructive = false), System.ComponentModel.Description("Create a plumbing pipe between two points (mm). pipeTypeId/systemTypeId/levelId default to first available / nearest level. Optional diameter (mm).")]
        public static async Task<string> CreatePipe(double startX, double startY, double startZ, double endX, double endY, double endZ, long? pipeTypeId = null, long? systemTypeId = null, long? levelId = null, double? diameter = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_pipe", new { start_x = startX, start_y = startY, start_z = startZ, end_x = endX, end_y = endY, end_z = endZ, pipe_type_id = pipeTypeId, system_type_id = systemTypeId, level_id = levelId, diameter });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_cable_tray", Destructive = false), System.ComponentModel.Description("Create an electrical cable tray between two points (mm). cableTrayTypeId/levelId default to first available / nearest level. Optional width+height (mm).")]
        public static async Task<string> CreateCableTray(double startX, double startY, double startZ, double endX, double endY, double endZ, long? cableTrayTypeId = null, long? levelId = null, double? width = null, double? height = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_cable_tray", new { start_x = startX, start_y = startY, start_z = startZ, end_x = endX, end_y = endY, end_z = endZ, cable_tray_type_id = cableTrayTypeId, level_id = levelId, width, height });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_conduit", Destructive = false), System.ComponentModel.Description("Create an electrical conduit between two points (mm). conduitTypeId/levelId default to first available / nearest level. Optional diameter (mm).")]
        public static async Task<string> CreateConduit(double startX, double startY, double startZ, double endX, double endY, double endZ, long? conduitTypeId = null, long? levelId = null, double? diameter = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_conduit", new { start_x = startX, start_y = startY, start_z = startZ, end_x = endX, end_y = endY, end_z = endZ, conduit_type_id = conduitTypeId, level_id = levelId, diameter });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_air_terminal", Destructive = false), System.ComponentModel.Description("Place an air terminal (diffuser/grille) family instance at a point (mm). typeId must be an Air Terminal FamilySymbol. Optional hostId for hosted placement on a duct/ceiling.")]
        public static async Task<string> CreateAirTerminal(long typeId, double x, double y, double z, long? levelId = null, long? hostId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_air_terminal", new { type_id = typeId, x, y, z, level_id = levelId, host_id = hostId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_lighting_fixture", Destructive = false), System.ComponentModel.Description("Place a lighting fixture family instance at a point (mm). typeId must be a Lighting Fixture FamilySymbol. Optional hostId for hosted placement on a ceiling.")]
        public static async Task<string> CreateLightingFixture(long typeId, double x, double y, double z, long? levelId = null, long? hostId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_lighting_fixture", new { type_id = typeId, x, y, z, level_id = levelId, host_id = hostId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "list_mep_systems", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List all MEP systems (mechanical/HVAC, piping/plumbing, electrical). domainFilter: all|mechanical|piping|electrical. Returns id, name, domain, system type, element count, connectivity status.")]
        public static async Task<string> ListMepSystems(string domainFilter = "all", int limit = 1000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_mep_systems", new { domain_filter = domainFilter, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_system_inventory", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Return the full element inventory of one MEP system: all member elements with category/type plus a category breakdown. Identify by systemId or systemName.")]
        public static async Task<string> GetSystemInventory(long? systemId = null, string systemName = "", bool includeParameters = false, int limit = 2000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_system_inventory", new { system_id = systemId, system_name = systemName, include_parameters = includeParameters, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_mep_element_connectors", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Inspect all connectors on an MEP element (duct/pipe/fitting/equipment/terminal): domain, shape, position (mm), connection status, flow, direction.")]
        public static async Task<string> GetMepElementConnectors(long elementId)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_mep_element_connectors", new { element_id = elementId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "connect_mep_elements", Destructive = false), System.ComponentModel.Description("Connect the nearest open connectors of two MEP elements. Optionally pin specific connectors via connectorIndex1/connectorIndex2 — these are Connector.Id values (the connector_id field from get_mep_element_connectors), NOT ordinals. Domains must match.")]
        public static async Task<string> ConnectMepElements(long elementId1, long elementId2, long? connectorIndex1 = null, long? connectorIndex2 = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("connect_mep_elements", new { element_id_1 = elementId1, element_id_2 = elementId2, connector_index_1 = connectorIndex1, connector_index_2 = connectorIndex2 });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_mep_fitting", Destructive = false), System.ComponentModel.Description("Insert an MEP fitting at connectors of existing MEP elements. fittingKind: elbow|tee|union|cross|transition. connectors is a JSON array of {element_id, connector_index} where connector_index is the connector_id from get_mep_element_connectors: elbow/union/transition need 2, tee 3, cross 4.")]
        public static async Task<string> CreateMepFitting(string fittingKind, string connectors)
        {
            try
            {
                var parsedConnectors = JArray.Parse(connectors);
                var result = await ToolGateway.SendToRevit("create_mep_fitting", new { fitting_kind = fittingKind, connectors = parsedConnectors });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "set_system_classification", Destructive = false), System.ComponentModel.Description("Add MEP elements to an existing duct/piping system. If systemId omitted, only reports current system membership (read-only). elementIds is an array of MEP element ids.")]
        public static async Task<string> SetSystemClassification(long[] elementIds, long? systemId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_system_classification", new { element_ids = elementIds, system_id = systemId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_panel_schedule", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read an electrical panel's circuit schedule: panel metadata, voltage, and the list of circuits with rating, load (VA), poles. Identify by panelId or panelName.")]
        public static async Task<string> GetPanelSchedule(long? panelId = null, string panelName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_panel_schedule", new { panel_id = panelId, panel_name = panelName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "find_mep_disconnects", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Find MEP elements with open/unconnected End connectors (potential gaps in ductwork, piping, conduit). domainFilter: all|hvac|piping|electrical. viewOnly restricts to the active view.")]
        public static async Task<string> FindMepDisconnects(string domainFilter = "all", bool viewOnly = false, int limit = 2000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("find_mep_disconnects", new { domain_filter = domainFilter, view_only = viewOnly, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "analyze_mep_network", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Analyze one MEP system's topology: category breakdown, connectivity health, base equipment, open connector count, and issues/recommendations. Identify by systemId or systemName.")]
        public static async Task<string> AnalyzeMepNetwork(long? systemId = null, string systemName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("analyze_mep_network", new { system_id = systemId, system_name = systemName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("graphics")]
    public class GraphicsTools
    {
        [McpServerTool(Name = "create_view_filter", Destructive = false), System.ComponentModel.Description("Create a parameter-based view filter (ParameterFilterElement) targeting one or more categories. rules is an optional JSON array of {parameter_name, evaluator, value} where evaluator is equals|not_equals|greater|less|contains|begins_with|ends_with. Omit rules for a category-only filter.")]
        public static async Task<string> CreateViewFilter(string name, string[] categories, string rules = "")
        {
            try
            {
                var parsedRules = string.IsNullOrWhiteSpace(rules) ? null : JArray.Parse(rules);
                var result = await ToolGateway.SendToRevit("create_view_filter", new { name, categories, rules = parsedRules });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "apply_filter_to_view", Destructive = false), System.ComponentModel.Description("Add an existing view filter (ParameterFilterElement) to a view's filter list. viewId defaults to the active view. visible sets the initial visibility of matching elements.")]
        public static async Task<string> ApplyFilterToView(long filterId, long? viewId = null, bool visible = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("apply_filter_to_view", new { filter_id = filterId, view_id = viewId, visible });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "set_filter_overrides", Destructive = false), System.ComponentModel.Description("Set graphic overrides for a filter already applied to a view. Colors are hex '#RRGGBB'. transparency 0-100, projectionLineWeight 1-16. Only supplied properties change; others are preserved. viewId defaults to active view.")]
        public static async Task<string> SetFilterOverrides(long filterId, long? viewId = null, string projectionLineColor = "", string surfaceForegroundColor = "", string cutLineColor = "", int? transparency = null, bool? halftone = null, int? projectionLineWeight = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_filter_overrides", new
                {
                    filter_id = filterId,
                    view_id = viewId,
                    projection_line_color = string.IsNullOrEmpty(projectionLineColor) ? null : projectionLineColor,
                    surface_foreground_color = string.IsNullOrEmpty(surfaceForegroundColor) ? null : surfaceForegroundColor,
                    cut_line_color = string.IsNullOrEmpty(cutLineColor) ? null : cutLineColor,
                    transparency,
                    halftone,
                    projection_line_weight = projectionLineWeight
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "list_view_filters", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List all view filter definitions (ParameterFilterElement) in the document. If viewId is supplied, only filters applied to that view. includeUsage lists which views each filter is applied to.")]
        public static async Task<string> ListViewFilters(long? viewId = null, bool includeUsage = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_view_filters", new { view_id = viewId, include_usage = includeUsage });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "remove_filter_from_view", Destructive = false), System.ComponentModel.Description("Remove a view filter from a view's filter list. viewId defaults to active view. deleteDefinitionIfUnused deletes the ParameterFilterElement entirely if no other view uses it.")]
        public static async Task<string> RemoveFilterFromView(long filterId, long? viewId = null, bool deleteDefinitionIfUnused = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("remove_filter_from_view", new { filter_id = filterId, view_id = viewId, delete_definition_if_unused = deleteDefinitionIfUnused });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "override_element_graphics", Destructive = false), System.ComponentModel.Description("Apply per-element view-specific graphic overrides (color, transparency, halftone, line weight) to elements in a view. Colors are hex '#RRGGBB'. transparency 0-100, projectionLineWeight 1-16. viewId defaults to active view.")]
        public static async Task<string> OverrideElementGraphics(long[] elementIds, long? viewId = null, string projectionLineColor = "", string surfaceForegroundColor = "", string cutLineColor = "", int? transparency = null, bool? halftone = null, int? projectionLineWeight = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("override_element_graphics", new
                {
                    element_ids = elementIds,
                    view_id = viewId,
                    projection_line_color = string.IsNullOrEmpty(projectionLineColor) ? null : projectionLineColor,
                    surface_foreground_color = string.IsNullOrEmpty(surfaceForegroundColor) ? null : surfaceForegroundColor,
                    cut_line_color = string.IsNullOrEmpty(cutLineColor) ? null : cutLineColor,
                    transparency,
                    halftone,
                    projection_line_weight = projectionLineWeight
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "clear_element_overrides", Destructive = false), System.ComponentModel.Description("Reset per-element view-specific graphic overrides to default. If elementIds is omitted, clears overrides on every element in the view that currently has them. viewId defaults to active view.")]
        public static async Task<string> ClearElementOverrides(long[] elementIds = null, long? viewId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("clear_element_overrides", new { element_ids = elementIds, view_id = viewId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "get_view_visibility", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Report a view's visibility/graphics state: hidden categories, applied filters, detail level, discipline, scale, view template, graphics-overrides-allowed. includeCategoryList lists every model category with its hidden state. viewId defaults to active view.")]
        public static async Task<string> GetViewVisibility(long? viewId = null, bool includeCategoryList = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_view_visibility", new { view_id = viewId, include_category_list = includeCategoryList });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "set_category_visibility", Destructive = false), System.ComponentModel.Description("Show or hide model categories in a view. categories is an array of category names. hidden=true hides, false shows. viewId defaults to active view.")]
        public static async Task<string> SetCategoryVisibility(string[] categories, bool hidden, long? viewId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_category_visibility", new { categories, hidden, view_id = viewId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "list_phases", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List all project phases (in sequence order) and all phase filters. Call before set_view_phase or set_element_phase to discover valid phase names/ids.")]
        public static async Task<string> ListPhases()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_phases", new { });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "set_view_phase", Destructive = false), System.ComponentModel.Description("Set a view's Phase and/or Phase Filter. Identify each by id or name. At least one of phase / phase filter must be supplied. viewId defaults to active view.")]
        public static async Task<string> SetViewPhase(long? viewId = null, long? phaseId = null, string phaseName = "", long? phaseFilterId = null, string phaseFilterName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_view_phase", new { view_id = viewId, phase_id = phaseId, phase_name = phaseName, phase_filter_id = phaseFilterId, phase_filter_name = phaseFilterName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "set_element_phase", Destructive = false), System.ComponentModel.Description("Set the Phase Created and/or Phase Demolished of elements. Identify phases by id or name. Use phaseDemolishedName='None' to clear demolition. At least one phase must be supplied.")]
        public static async Task<string> SetElementPhase(long[] elementIds, long? phaseCreatedId = null, string phaseCreatedName = "", long? phaseDemolishedId = null, string phaseDemolishedName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_element_phase", new { element_ids = elementIds, phase_created_id = phaseCreatedId, phase_created_name = phaseCreatedName, phase_demolished_id = phaseDemolishedId, phase_demolished_name = phaseDemolishedName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("toolbaker")]
    public class ToolbakerTools
    {
        [McpServerTool(Name = "send_code_to_revit"), System.ComponentModel.Description("Compile + run C# inside Revit for workflows not covered by typed tools. Variables: doc (Document), uidoc (UIDocument), app (UIApplication). Write body only, auto-wrapped in static Run(UIApplication). Must end with 'return ...;'. Namespaces: System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB, Autodesk.Revit.UI. Common patterns: FilteredElementCollector for queries, Transaction for mutations, UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters), uidoc.Selection.SetElementIds(), OverrideGraphicSettings.")]
        public static async Task<string> SendCodeToRevit(string code)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("send_code_to_revit", new { code });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "list_baked_tools", ReadOnly = true, Idempotent = true), System.ComponentModel.Description(
            "List all baked tools with name, description, usage count, creation date. " +
            "Call before run_baked_tool to discover available tools.")]
        public static async Task<string> ListBakedTools()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_baked_tools");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "run_baked_tool"), System.ComponentModel.Description(
            "Run a baked tool by name. Call list_baked_tools first to discover. " +
            "Params: name (baked tool name), params (object, tool-specific).")]
        public static async Task<string> RunBakedTool(string name, object @params = null)
        {
            var revitVersionBeforeConnect = ToolGateway.CurrentRevitVersion ?? AuthToken.Target ?? "unknown";
            try
            {
                var normalizedParams = NormalizeRunBakedToolParams(@params);
                var result = await ToolGateway.SendToRevit("run_baked_tool", new { name, @params = normalizedParams });
                var revitVersion = ToolGateway.CurrentRevitVersion ?? revitVersionBeforeConnect;
                RecordBakedToolRun(name, revitVersion, success: true, error: null);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                var revitVersion = ToolGateway.CurrentRevitVersion ?? revitVersionBeforeConnect;
                RecordBakedToolRun(name, revitVersion, success: false, error: ex.Message);
                return $"Error: {ex.Message}";
            }
        }

        public static JObject NormalizeRunBakedToolParams(object @params)
        {
            if (@params == null)
                return new JObject();

            if (@params is JObject obj)
                return obj;

            if (@params is JToken token)
            {
                if (token is JObject tokenObj)
                    return tokenObj;
                throw new ArgumentException("params must be a JSON object.");
            }

            if (!(@params is JsonElement element))
            {
                var converted = JToken.FromObject(@params);
                if (converted is JObject convertedObj)
                    return convertedObj;
                throw new ArgumentException("params must be a JSON object.");
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Undefined:
                case JsonValueKind.Null:
                    return new JObject();
                case JsonValueKind.Object:
                    return JObject.Parse(element.GetRawText());
                default:
                    throw new ArgumentException("params must be a JSON object.");
            }
        }

        private static void RecordBakedToolRun(string name, string revitVersion, bool success, string error)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            try
            {
                using var db = new BakeDb(new BakePaths());
                db.Migrate();
                db.TryRecordRegistryRun(name, revitVersion, success, error, DateTimeOffset.UtcNow);
            }
            catch
            {
                // Server owns durable bake.db writes, but run_baked_tool should not fail
                // just because lifecycle stats could not be updated.
            }
        }
    }

    [McpServerToolType, Toolset("toolbaker")]
    public class AdaptiveBakeTools
    {
        [McpServerTool(Name = "list_bake_suggestions", ReadOnly = true, Idempotent = true), System.ComponentModel.Description(
            "List adaptive ToolBaker suggestions. Returns suggestions with id, title, source, score, state, output choices, and creation time.")]
        public static string ListBakeSuggestions()
        {
            try
            {
                var paths = new BakePaths();
                using var db = new BakeDb(paths);
                db.Migrate();
                return ListBakeSuggestionsHandler.Handle(db, ToolGateway.UsageLogger);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "accept_bake_suggestion"), System.ComponentModel.Description(
            "Accept an adaptive ToolBaker suggestion by id. Validates name, schema, and output choice, then prepares a bake request without native tool promotion.")]
        public static async Task<string> AcceptBakeSuggestion(string id, string name, string output_choice = "mcp_only", string params_schema = null)
        {
            try
            {
                var paths = new BakePaths();
                using var db = new BakeDb(paths);
                db.Migrate();
                return await AcceptBakeSuggestionHandler.HandleAsync(
                    db,
                    id,
                    name,
                    output_choice,
                    params_schema,
                    pluginApply: request => ToolGateway.SendToRevit("apply_bake", request));
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "dismiss_bake_suggestion"), System.ComponentModel.Description(
            "Dismiss an adaptive ToolBaker suggestion. action must be snooze_30d, never, or never_with_gap_signal.")]
        public static string DismissBakeSuggestion(string id, string action)
        {
            try
            {
                var paths = new BakePaths();
                using var db = new BakeDb(paths);
                db.Migrate();
                var revitVersion = ToolGateway.CurrentRevitVersion ?? AuthToken.Target ?? "unknown";
                return DismissBakeSuggestionHandler.Handle(
                    db,
                    id,
                    action,
                    currentRevitVersion: revitVersion,
                    auditLog: new ToolBakerAuditLog(paths.AuditJsonl));
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("meta")]
    public class MetaTools
    {
        [McpServerTool(Name = "show_message", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Show a Revit TaskDialog. For connection tests or user notifications. Both 'message' and 'title' are optional — omit for default greeting.")]
        public static async Task<string> ShowMessage(string message = null, string title = null)
        {
            try
            {
                object parameters = null;
                if (!string.IsNullOrWhiteSpace(message) || !string.IsNullOrWhiteSpace(title))
                {
                    parameters = new { message, title };
                }
                var result = await ToolGateway.SendToRevit("show_message", parameters);
                return JsonConvert.SerializeObject(result);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "switch_target"), System.ComponentModel.Description(
            "Switch active Revit connection to a specific version when multiple Revits are running. " +
            "version: 'R22'|'R23'|'R24'|'R25'|'R26'|'R27' to pin, or 'auto' to clear pin and re-enable auto-detect. " +
            "Immediately closes the current Server↔Plugin connection (cancels in-flight requests) and updates the target. " +
            "The next tool call transparently reconnects against the new target — no user confirmation required. " +
            "Returns: {ok, previousTarget, newTarget, verified (if verify=true)}. " +
            "verify=true (default): immediately attempts get_current_view_info against the new target to confirm connectivity; " +
            "set verify=false to skip when the new target's document isn't in a view yet (e.g., Revit just launched).")]
        public static async Task<string> SwitchTarget(string version, bool verify = true)
        {
            try
            {
                var previousTarget = AuthToken.Target;
                string newTarget = null;
                if (!string.IsNullOrWhiteSpace(version) && !version.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    var upper = version.Trim().ToUpperInvariant();
                    if (Array.IndexOf(AuthToken.AllVersions, upper) < 0)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            ok = false,
                            error = $"Invalid version '{version}'. Allowed: {string.Join(",", AuthToken.AllVersions)} or 'auto'."
                        });
                    }
                    newTarget = upper;
                }

                ToolGateway.Reconnect(newTarget);

                if (!verify)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        ok = true,
                        previousTarget = previousTarget ?? "auto",
                        newTarget = newTarget ?? "auto",
                        verified = false,
                        note = "Target updated. Next tool call will connect to new target."
                    });
                }

                try
                {
                    var probe = await ToolGateway.SendToRevit("get_current_view_info");
                    return JsonConvert.SerializeObject(new
                    {
                        ok = true,
                        previousTarget = previousTarget ?? "auto",
                        newTarget = newTarget ?? "auto",
                        verified = true,
                        activeView = probe.Value<string>("viewName")
                    });
                }
                catch (Exception verifyEx)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        ok = true,
                        previousTarget = previousTarget ?? "auto",
                        newTarget = newTarget ?? "auto",
                        verified = false,
                        verifyError = verifyEx.Message,
                        note = "Target set, but verify failed. The next tool call may still succeed."
                    });
                }
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "batch_execute"), System.ComponentModel.Description(
            "Run multiple MCP commands atomically inside one Revit TransactionGroup (single undo on success). " +
            "Input: commands — JSON array of {command, params}, e.g. " +
            "'[{\"command\":\"create_level\",\"params\":{\"elevation\":3000}}, " +
            "{\"command\":\"create_grid\",\"params\":{\"startX\":0,\"startY\":0,\"endX\":5000,\"endY\":0}}]'. " +
            "On any failure the whole group rolls back unless continueOnError=true. " +
            "Returns: {results: [{index, ok, data|error}], rolledBack}.")]
        public static async Task<string> BatchExecute(string commands, bool continueOnError = false)
        {
            try
            {
                var parsed = JArray.Parse(commands);
                var result = await ToolGateway.SendToRevit("batch_execute", new { commands = parsed, continueOnError });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "analyze_usage_patterns", ReadOnly = true, Idempotent = true), System.ComponentModel.Description(
            "Analyze MCP tool usage. Returns session stats (call counts, success rates, top tools, flags) " +
            "plus historical data from journal files. " +
            "Params: days (int, default 1) — days of history to include. " +
            "Use to spot most-used tools, frequent failures, repeated patterns.")]
        public static string AnalyzeUsagePatterns(int days = 1)
        {
            try
            {
                var session = ToolGateway.Session;
                if (session == null) return JsonConvert.SerializeObject(new { error = "No active session" });

                var report = session.GetPatternReport();

                var journal = session.Journal;
                var historicalTools = new Dictionary<string, int>();
                var historicalErrors = new Dictionary<string, int>();
                int historicalTotal = 0;

                var dates = journal.ListDates();
                var cutoff = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");

                foreach (var date in dates)
                {
                    if (string.Compare(date, cutoff, StringComparison.Ordinal) < 0) continue;
                    var entries = journal.ReadDay(date);
                    foreach (var entry in entries)
                    {
                        historicalTotal++;
                        if (!historicalTools.ContainsKey(entry.Tool)) historicalTools[entry.Tool] = 0;
                        historicalTools[entry.Tool]++;
                        if (!entry.Success)
                        {
                            if (!historicalErrors.ContainsKey(entry.Tool)) historicalErrors[entry.Tool] = 0;
                            historicalErrors[entry.Tool]++;
                        }
                    }
                }

                var result = new
                {
                    session = new
                    {
                        total_calls = report.TotalCalls,
                        total_errors = report.TotalErrors,
                        top_tools = report.TopTools.Select(t => new { t.Tool, t.CallCount, t.ErrorCount, error_rate = t.ErrorRate.ToString("P0") }),
                        error_prone = report.ErrorProne.Select(t => new { t.Tool, t.CallCount, t.ErrorCount, error_rate = t.ErrorRate.ToString("P0") }),
                        flags = report.Flags
                    },
                    history = new
                    {
                        days_included = days,
                        total_calls = historicalTotal,
                        top_tools = historicalTools.OrderByDescending(kv => kv.Value).Take(10)
                            .Select(kv => new { tool = kv.Key, count = kv.Value }),
                        error_tools = historicalErrors.OrderByDescending(kv => kv.Value).Take(5)
                            .Select(kv => new { tool = kv.Key, errors = kv.Value })
                    }
                };

                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }

    [McpServerToolType, Toolset("lint")]
    public class LintTools
    {
        [McpServerTool(Name = "analyze_view_naming_patterns", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Infer dominant view-naming pattern from project. Returns patterns with coverage + outliers. Zero args. Use before suggest_view_name_corrections.")]
        public static async Task<string> AnalyzeViewNamingPatterns()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("analyze_view_naming_patterns");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "suggest_view_name_corrections", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Propose corrected view names for outliers. Optional profile=<id> uses firm-profile library rule; omit to use project-inferred dominant pattern. Returns suggestions array with id/current/suggested/reason.")]
        public static async Task<string> SuggestViewNameCorrections(string profile = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("suggest_view_name_corrections", new { profile });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "detect_firm_profile", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Fingerprint project naming (views + sheets + levels), match against firm-profile library. Returns project_pattern (always) + library_match (null if library empty or no match).")]
        public static async Task<string> DetectFirmProfile()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("detect_firm_profile");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }
}
