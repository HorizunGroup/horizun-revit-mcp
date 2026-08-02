using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;
using Horizun.Revit.Core;

namespace Horizun.Server.Tests
{
    public sealed class PowerBiPushTests
    {
        [Fact]
        public void DryRun_ValidatesWithoutCallingMicrosoft()
        {
            var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using (var client = new HttpClient(handler))
            {
                JObject result = PowerBiPush.Handle(Request(true), client, Ledger(out string dir), EnvToken);
                try
                {
                    Assert.True(result.Value<bool>("dry_run"));
                    Assert.Equal(2, result.Value<int>("rows_validated"));
                    Assert.Equal("access_token", result.Value<string>("auth_mode"));
                    Assert.Equal(0, handler.Calls);
                }
                finally { Directory.Delete(dir, true); }
            }
        }

        [Fact]
        public void Apply_SendsOnceAndIdenticalRetryReplays()
        {
            var handler = new RecordingHandler(request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("https://api.powerbi.com/v1.0/myorg/datasets/11111111-1111-1111-1111-111111111111/tables/Elements/rows", request.RequestUri.ToString());
                Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
                Assert.Equal("secret-test-token", request.Headers.Authorization.Parameter);
                JObject body = JObject.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Equal(2, ((JArray)body["rows"]).Count);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            using (var client = new HttpClient(handler))
            {
                DurableCommandLedger ledger = Ledger(out string dir);
                try
                {
                    JObject request = Request(false);
                    JObject first = PowerBiPush.Handle(request, client, ledger, EnvToken);
                    // Replay is a ledger read, not a fresh authentication attempt. It must
                    // still work after the short-lived token has been removed/rotated.
                    JObject replay = PowerBiPush.Handle((JObject)request.DeepClone(), client, ledger, _ => null);
                    Assert.Equal("accepted_by_power_bi", first.Value<string>("delivery"));
                    Assert.Equal(first.ToString(), replay.ToString());
                    Assert.Equal(1, handler.Calls);
                }
                finally { Directory.Delete(dir, true); }
            }
        }

        [Fact]
        public void LostResponse_LeavesKeyInDoubtAndNeverSendsAgain()
        {
            var handler = new RecordingHandler(_ => throw new HttpRequestException("connection reset after upload"));
            using (var client = new HttpClient(handler))
            {
                DurableCommandLedger ledger = Ledger(out string dir);
                try
                {
                    JObject request = Request(false);
                    ToolRefusal first = Assert.Throws<ToolRefusal>(() => PowerBiPush.Handle(request, client, ledger, EnvToken));
                    Assert.Contains("in_doubt", first.Message);
                    ToolRefusal retry = Assert.Throws<ToolRefusal>(() => PowerBiPush.Handle(request, client, ledger, EnvToken));
                    Assert.Contains("will NOT repeat", retry.Message);
                    Assert.Equal(1, handler.Calls);
                }
                finally { Directory.Delete(dir, true); }
            }
        }

        [Fact]
        public void DefinitiveHttpFailure_IsRecordedAndRetryDoesNotSendAgain()
        {
            var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                ReasonPhrase = "Bad Request",
                Content = new StringContent("tenant-sensitive diagnostic that must not be echoed")
            });
            using (var client = new HttpClient(handler))
            {
                DurableCommandLedger ledger = Ledger(out string dir);
                try
                {
                    JObject request = Request(false);
                    ToolRefusal first = Assert.Throws<ToolRefusal>(() => PowerBiPush.Handle(request, client, ledger, EnvToken));
                    Assert.Contains("HTTP 400", first.Message);
                    Assert.DoesNotContain("tenant-sensitive", first.Message);

                    ToolRefusal replay = Assert.Throws<ToolRefusal>(() => PowerBiPush.Handle(request, client, ledger, _ => null));
                    Assert.Equal(first.Message, replay.Message);
                    Assert.Equal(1, handler.Calls);
                }
                finally { Directory.Delete(dir, true); }
            }
        }

        [Fact]
        public void NestedValuesAreRefusedBeforeNetworkOrLedger()
        {
            JObject request = Request(false);
            ((JObject)((JArray)request["rows"])[0])["bad"] = new JObject { ["nested"] = true };
            var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using (var client = new HttpClient(handler))
            {
                DurableCommandLedger ledger = Ledger(out string dir);
                try
                {
                    ToolRefusal refusal = Assert.Throws<ToolRefusal>(() => PowerBiPush.Handle(request, client, ledger, EnvToken));
                    Assert.Contains("nested objects/arrays", refusal.Message);
                    Assert.Equal(0, handler.Calls);
                    Assert.Empty(Directory.GetFiles(dir));
                }
                finally { Directory.Delete(dir, true); }
            }
        }

        [Fact]
        public void NonFiniteNumbersAreRefusedBeforeNetworkOrLedger()
        {
            JObject request = Request(false);
            ((JObject)((JArray)request["rows"])[0])["bad"] = double.NaN;
            var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using (var client = new HttpClient(handler))
            {
                DurableCommandLedger ledger = Ledger(out string dir);
                try
                {
                    ToolRefusal refusal = Assert.Throws<ToolRefusal>(() => PowerBiPush.Handle(request, client, ledger, EnvToken));
                    Assert.Contains("finite JSON number", refusal.Message);
                    Assert.Equal(0, handler.Calls);
                    Assert.Empty(Directory.GetFiles(dir));
                }
                finally { Directory.Delete(dir, true); }
            }
        }

        private static JObject Request(bool dryRun) => new JObject
        {
            ["dataset_id"] = "11111111-1111-1111-1111-111111111111",
            ["table"] = "Elements",
            ["rows"] = new JArray
            {
                new JObject { ["ElementId"] = 1, ["Category"] = "Walls" },
                new JObject { ["ElementId"] = 2, ["Category"] = "Floors" }
            },
            ["dry_run"] = dryRun,
            ["idempotency_key"] = "test-" + Guid.NewGuid().ToString("N")
        };

        private static string EnvToken(string name) => name == "HORIZUN_POWER_BI_ACCESS_TOKEN" ? "secret-test-token" : null;

        private static DurableCommandLedger Ledger(out string directory)
        {
            directory = Path.Combine(Path.GetTempPath(), "horizun-pbi-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string captured = directory;
            return new DurableCommandLedger(() => captured, () => new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc), () => 42);
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
            public int Calls;
            public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) { _answer = answer; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                try { return Task.FromResult(_answer(request)); }
                catch (Exception ex) { return Task.FromException<HttpResponseMessage>(ex); }
            }
        }
    }
}
