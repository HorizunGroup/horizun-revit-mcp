using System;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// horizun_request_python_access returns at once (field report: it held Revit for up
    /// to 600 s). The ledger behind pending_approval and operation=status.
    /// </summary>
    [Collection("PythonAccessRequests")]
    public sealed class PythonAccessRequestsTests : IDisposable
    {
        private DateTime _now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

        public PythonAccessRequestsTests()
        {
            PythonAccessRequests.ResetForTests();
            PythonAccessRequests.Clock = () => _now;
        }

        public void Dispose() => PythonAccessRequests.ResetForTests();

        [Fact]
        public void A_request_is_pending_at_once_and_the_reply_says_how_to_poll()
        {
            PythonAccessRequest r = PythonAccessRequests.Open("need a script", out bool reused);
            Assert.False(reused);
            Assert.Equal(PythonAccessRequests.Pending, r.State);

            JObject reply = PythonAccessStatus.Reply(PythonAccessRequests.ToJson(r), allowedNow: false);
            Assert.False((bool)reply["granted"]);
            Assert.Equal("pending_approval", (string)reply["decision"]);
            Assert.Equal("pending_approval", (string)reply["state"]);
            Assert.False((bool)reply["blocks_revit"]);
            Assert.Equal(r.Id, (string)reply["request_id"]);
            Assert.Equal("status", (string)reply["poll"]["arguments"]["operation"]);
            Assert.Equal(r.Id, (string)reply["poll"]["arguments"]["request_id"]);
        }

        [Fact]
        public void A_second_request_while_one_is_pending_reuses_it_instead_of_stacking_windows()
        {
            PythonAccessRequest a = PythonAccessRequests.Open(null, out _);
            PythonAccessRequest b = PythonAccessRequests.Open("again", out bool reused);
            Assert.True(reused);
            Assert.Equal(a.Id, b.Id);
        }

        [Fact]
        public void Only_a_pending_request_moves_and_a_decision_is_final()
        {
            PythonAccessRequest r = PythonAccessRequests.Open(null, out _);
            Assert.True(PythonAccessRequests.Decide(r.Id, PythonAccessRequests.Declined, "no"));
            Assert.False(PythonAccessRequests.Decide(r.Id, PythonAccessRequests.Granted));
            Assert.Equal(PythonAccessRequests.Declined, PythonAccessRequests.Get(r.Id).State);
            Assert.False(PythonAccessRequests.Decide(r.Id, "pending"));
            Assert.False(PythonAccessRequests.Decide("pyreq-unknown", PythonAccessRequests.Granted));

            JObject reply = PythonAccessStatus.Reply(PythonAccessRequests.ToJson(PythonAccessRequests.Get(r.Id)), false);
            Assert.Equal("rejected_or_cancelled_by_owner", (string)reply["decision"]);
            Assert.Null(reply["poll"]?.Type == JTokenType.Null ? null : reply["poll"]);
        }

        [Fact]
        public void An_unanswered_request_expires_and_a_new_one_can_be_opened()
        {
            PythonAccessRequest r = PythonAccessRequests.Open(null, out _);
            _now += PythonAccessRequests.Lifetime + TimeSpan.FromSeconds(1);
            Assert.Equal(PythonAccessRequests.Expired, PythonAccessRequests.Get(r.Id).State);
            Assert.Equal("expired_unanswered",
                (string)PythonAccessStatus.Reply(PythonAccessRequests.ToJson(PythonAccessRequests.Get(r.Id)), false)["decision"]);
            PythonAccessRequest next = PythonAccessRequests.Open(null, out bool reused);
            Assert.False(reused);
            Assert.NotEqual(r.Id, next.Id);
        }

        [Fact]
        public void Granted_is_the_setting_now_not_the_request_alone()
        {
            PythonAccessRequest r = PythonAccessRequests.Open(null, out _);
            PythonAccessRequests.Decide(r.Id, PythonAccessRequests.Granted, "ok");
            JObject row = PythonAccessRequests.ToJson(PythonAccessRequests.Get(r.Id));

            JObject on = PythonAccessStatus.Reply(row, allowedNow: true);
            Assert.True((bool)on["granted"]);
            Assert.Equal("enabled_by_owner", (string)on["decision"]);

            // Revoked afterwards by the owner, or a machine policy overrides it: not ON.
            JObject off = PythonAccessStatus.Reply(row, allowedNow: false);
            Assert.False((bool)off["granted"]);
            Assert.Equal("granted_then_revoked", (string)off["decision"]);
        }

        [Fact]
        public void Health_carries_the_latest_request()
        {
            Assert.Null(PythonAccessRequests.Snapshot());
            PythonAccessRequest r = PythonAccessRequests.Open(null, out _);
            JObject snap = PythonAccessRequests.Snapshot();
            Assert.Equal(r.Id, (string)snap["request_id"]);
            Assert.Equal("pending", (string)snap["state"]);
            Assert.NotNull(snap["expires_utc"]);
        }

        [Fact]
        public void No_request_and_python_off_says_so()
        {
            JObject reply = PythonAccessStatus.Reply(null, false);
            Assert.Equal("no_request", (string)reply["decision"]);
            Assert.False((bool)reply["granted"]);
            Assert.Equal("already_enabled", (string)PythonAccessStatus.Reply(null, true)["decision"]);
        }
    }
}
