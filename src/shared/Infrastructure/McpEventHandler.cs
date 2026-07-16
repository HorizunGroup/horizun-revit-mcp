using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using RvtMcp.Plugin.Views.Toast;

namespace RvtMcp.Plugin
{
    public class PendingRequest
    {
        public string Id { get; set; }
        public string CommandName { get; set; }
        public string ParamsJson { get; set; }
        public TaskCompletionSource<string> Tcs { get; set; }
    }

    public class McpEventHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<PendingRequest> _queue = new ConcurrentQueue<PendingRequest>();
        private readonly CommandDispatcher _dispatcher;
        private readonly McpSessionLog _sessionLog;
        private readonly McpToastNotifier _toastNotifier;

        public McpEventHandler(CommandDispatcher dispatcher, McpSessionLog sessionLog, McpToastNotifier toastNotifier = null)
        {
            _dispatcher = dispatcher;
            _sessionLog = sessionLog;
            _toastNotifier = toastNotifier;

            try
            {
                var config = RvtMcpConfig.Load(args: null);
                SendCodeJournal.RunMaintenance(config);
            }
            catch { }
        }

        public void Enqueue(PendingRequest request)
        {
            _queue.Enqueue(request);
        }

        public void Execute(UIApplication app)
        {
            App.Instance?.CaptureMainWindowHandle(app.MainWindowHandle);

            while (_queue.TryDequeue(out var request))
            {
                // Stale command guard: skip if TCS already completed (timeout/cancel)
                if (request.Tcs.Task.IsCompleted)
                    continue;

                var sw = Stopwatch.StartNew();
                try
                {
                    var command = _dispatcher.GetCommand(request.CommandName);
                    if (command == null)
                    {
                        sw.Stop();
                        var unknownError = McpResponsePrivacy.RedactErrorForResponse($"Unknown command: {request.CommandName}");
                        McpLogger.Log(request.CommandName, request.ParamsJson, false,
                                      sw.ElapsedMilliseconds, unknownError);
                        _sessionLog?.Add(new McpCallEntry
                        {
                            ToolName = request.CommandName,
                            ParamsJson = request.ParamsJson,
                            Success = false,
                            DurationMs = sw.ElapsedMilliseconds,
                            ErrorMessage = unknownError,
                            Summary = $"Unknown: {request.CommandName}"
                        });
                        var errorResponse = JsonConvert.SerializeObject(new
                        {
                            id = request.Id,
                            success = false,
                            error = unknownError
                        });
                        request.Tcs.TrySetResult(errorResponse);
                        try
                        {
                            _toastNotifier?.OnCompleted(
                                request.CommandName,
                                request.ParamsJson,
                                null,
                                false,
                                unknownError,
                                sw.ElapsedMilliseconds,
                                null);
                        }
                        catch (Exception toastEx)
                        {
                            App.DebugLog("McpToastNotifier.OnCompleted failed (unknown command): " + toastEx.Message);
                        }
                        continue;
                    }

                    // S6 strict schema validation — fail fast with error-as-teacher envelope
                    var validation = SchemaValidator.Validate(command.ParametersSchema, request.ParamsJson);
                    if (!validation.IsValid)
                    {
                        sw.Stop();
                        var validationError = McpResponsePrivacy.RedactErrorForResponse(validation.Error);
                        McpLogger.Log(request.CommandName, request.ParamsJson, false,
                                      sw.ElapsedMilliseconds, validationError);
                        _sessionLog?.Add(new McpCallEntry
                        {
                            ToolName = request.CommandName,
                            ParamsJson = request.ParamsJson,
                            Success = false,
                            DurationMs = sw.ElapsedMilliseconds,
                            ErrorMessage = validationError,
                            ToolDescription = command.Description,
                            Summary = "Validation failed: " + validationError
                        });
                        var validationResponse = JsonConvert.SerializeObject(new
                        {
                            id = request.Id,
                            success = false,
                            error = validationError,
                            suggestion = validation.Suggestion,
                            hint = validation.Hint
                        });
                        request.Tcs.TrySetResult(validationResponse);
                        try
                        {
                            _toastNotifier?.OnCompleted(
                                request.CommandName,
                                request.ParamsJson,
                                null,
                                false,
                                validationError,
                                sw.ElapsedMilliseconds,
                                command.Description);
                        }
                        catch (Exception toastEx)
                        {
                            App.DebugLog("McpToastNotifier.OnCompleted failed (validation): " + toastEx.Message);
                        }
                        continue;
                    }

                    // Horizun hardening: (A) suppress modal dialogs only while the
                    // command runs on the UI thread, and (C) detect a leaked/orphaned
                    // transaction the moment the command returns. The dialog guard
                    // also auto-dismisses the "transaction discarded" modal that a
                    // leaked transaction would otherwise raise and freeze the pump.
                    CommandResult result;
                    McpDialogGuard.IsMcpExecuting = true;
                    try
                    {
                        result = command.Execute(app, request.ParamsJson);
                    }
                    finally
                    {
                        McpDialogGuard.IsMcpExecuting = false;
                        try
                        {
                            var sweepDoc = app.ActiveUIDocument?.Document;
                            if (sweepDoc != null && sweepDoc.IsModifiable)
                                App.DebugLog($"McpTransaction: command '{request.CommandName}' left a transaction " +
                                    "OPEN (document still modifiable). Handlers must wrap edits in using(Transaction) " +
                                    "so a throw auto-rolls-back; a dangling transaction blocks every later command.");
                        }
                        catch { }
                    }
                    sw.Stop();
                    // responseData is the redacted view used ONLY for session log + summary.
                    // The wire response (below) uses result.Data raw so the agent sees real values.
                    var responseData = McpResponsePrivacy.RedactDataForResponse(request.CommandName, result.Data);

                    string codeSnippet = null;
                    if (request.CommandName == "send_code_to_revit")
                    {
                        try { codeSnippet = JObject.Parse(request.ParamsJson)?.Value<string>("code"); }
                        catch { }
                    }

                    // Serialize result for logging
                    string resultJson = null;
                    try { resultJson = result.Data != null ? JsonConvert.SerializeObject(result.Data) : null; }
                    catch { }

                    string responseResultJson = null;
                    try { responseResultJson = responseData != null ? JsonConvert.SerializeObject(responseData) : null; }
                    catch { }

                    // Truncate for session log (max 10KB)
                    string sessionResult = responseResultJson != null && responseResultJson.Length > 10240
                        ? responseResultJson.Substring(0, 10240)
                        : responseResultJson;

                    var resultError = McpResponsePrivacy.RedactErrorForResponse(result.Error);
                    McpLogger.Log(request.CommandName, request.ParamsJson, result.Success,
                                  sw.ElapsedMilliseconds, resultError, codeSnippet, resultJson);

                    SendCodeJournalGate.OnSendCodeLogged(request.CommandName, request.ParamsJson, codeSnippet, result.Success, sw.ElapsedMilliseconds, resultError, resultJson);

                    _sessionLog?.Add(new McpCallEntry
                    {
                        ToolName = request.CommandName,
                        ParamsJson = request.ParamsJson,
                        Success = result.Success,
                        DurationMs = sw.ElapsedMilliseconds,
                        ErrorMessage = resultError,
                        CodeSnippet = codeSnippet,
                        ResultJson = sessionResult,
                        ToolDescription = command.Description,
                        Summary = SummaryGenerator.Generate(request.CommandName, request.ParamsJson,
                                                             sessionResult, result.Success, resultError)
                    });

                    var response = JsonConvert.SerializeObject(new
                    {
                        id = request.Id,
                        success = result.Success,
                        data = result.Data,
                        error = resultError
                    });

                    var sizeWarning = ResponseSizeGuard.CheckResponse(
                        commandName: request.CommandName,
                        serializedPayload: response,
                        topLevelKeyCount: (result.Data as Newtonsoft.Json.Linq.JObject)?.Count ?? 0);
                    if (sizeWarning != null)
                    {
                        Console.Error.WriteLine(sizeWarning);
                    }

                    request.Tcs.TrySetResult(response);
                    try
                    {
                        // Use redacted payload (same policy as session log) — never raw result.Data.
                        _toastNotifier?.OnCompleted(
                            request.CommandName,
                            request.ParamsJson,
                            responseResultJson,
                            result.Success,
                            resultError,
                            sw.ElapsedMilliseconds,
                            command.Description);
                    }
                    catch (Exception toastEx)
                    {
                        App.DebugLog("McpToastNotifier.OnCompleted failed (success path): " + toastEx.Message);
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    var exError = McpResponsePrivacy.RedactErrorForResponse(ex.Message);
                    McpLogger.Log(request.CommandName, request.ParamsJson, false,
                                  sw.ElapsedMilliseconds, exError);

                    string codeSnippetEx = null;
                    if (request.CommandName == "send_code_to_revit")
                    {
                        try { codeSnippetEx = JObject.Parse(request.ParamsJson)?.Value<string>("code"); }
                        catch { }
                    }
                    SendCodeJournalGate.OnSendCodeLogged(request.CommandName, request.ParamsJson, codeSnippetEx, false, sw.ElapsedMilliseconds, exError, null);

                    _sessionLog?.Add(new McpCallEntry
                    {
                        ToolName = request.CommandName,
                        ParamsJson = request.ParamsJson,
                        Success = false,
                        DurationMs = sw.ElapsedMilliseconds,
                        ErrorMessage = exError,
                        Summary = SummaryGenerator.Generate(request.CommandName, request.ParamsJson,
                                                             null, false, exError)
                    });
                    var errorResponse = JsonConvert.SerializeObject(new
                    {
                        id = request.Id,
                        success = false,
                        error = exError
                    });
                    request.Tcs.TrySetResult(errorResponse);
                    try
                    {
                        _toastNotifier?.OnCompleted(
                            request.CommandName,
                            request.ParamsJson,
                            null,
                            false,
                            exError,
                            sw.ElapsedMilliseconds,
                            null);
                    }
                    catch (Exception toastEx)
                    {
                        App.DebugLog("McpToastNotifier.OnCompleted failed (exception path): " + toastEx.Message);
                    }
                }
            }
        }

        public string GetName() => "RvtMcp.McpEventHandler";

        public void CancelAll()
        {
            while (_queue.TryDequeue(out var request))
            {
                request.Tcs.TrySetCanceled();
            }
        }
    }
}
