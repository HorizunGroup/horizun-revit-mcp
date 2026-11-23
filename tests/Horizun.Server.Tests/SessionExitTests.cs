using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class SessionExitTests
    {
        private const string Owner = "d97a723455f9478490542c760a617b38";
        private const string Started = "2026-11-18T12:00:00.0000000Z";
        private static JObject Request() => new JObject { ["operation"] = "exit_application", ["expected_pid"] = 123,
            ["expected_started_utc"] = Started, ["owner_session"] = Owner };

        [Fact]
        public void Only_the_exact_task_launch_is_admitted()
        {
            Assert.Null(SessionExitRules.OwnershipError(Request(), 123, Started, Owner));
            Assert.NotNull(SessionExitRules.OwnershipError(Request(), 123, Started, null));
            Assert.NotNull(SessionExitRules.OwnershipError(Request(), 124, Started, Owner));
            Assert.Null(SessionExitRules.OwnershipError(Request(), 123, "2026-11-18T12:00:01Z", Owner));
            Assert.NotNull(SessionExitRules.OwnershipError(Request(), 123, "2026-11-18T12:00:01.001Z", Owner));
            Assert.NotNull(SessionExitRules.OwnershipError(Request(), 123, Started, "other-task"));
        }

        [Theory]
        [InlineData("expected_pid")]
        [InlineData("expected_started_utc")]
        [InlineData("owner_session")]
        public void Missing_process_evidence_is_never_assumed(string field)
        {
            var r = Request(); r.Remove(field);
            Assert.NotNull(SessionExitRules.Validate(r));
        }

        [Fact]
        public void Unrelated_arguments_and_invalid_discard_lists_are_refused()
        {
            var r = Request(); r["discard_documents"] = new JArray(12);
            Assert.NotNull(SessionExitRules.Validate(r));
            r.Remove("discard_documents"); r["save_on_close"] = true;
            Assert.NotNull(ToolInputRules.ValidateSession(r, "exit_application"));
            Assert.Empty(ContractSchemaCheck.Validate(Request(), Contract.Find("horizun_document_session").InputSchema));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, true)]
        [InlineData(null, false, true)]
        [InlineData(false, true, true)]
        public void Discard_requirement_ready_and_foreign_fingerprints_are_pure(bool? modified, bool pending, bool required)
        {
            Assert.Equal(required, SessionExitRules.DiscardRequired(modified, pending ? new JObject() : null));
            var states = JArray.Parse("[{'fingerprint':'ours','discard_required':true,'discard_authorized':false}]");
            Assert.False(SessionExitRules.Ready(states));
            states[0]["discard_authorized"] = true; Assert.True(SessionExitRules.Ready(states));
            Assert.Null(SessionExitRules.ForeignDiscard(states, new[] { "ours" }));
            Assert.NotNull(SessionExitRules.ForeignDiscard(states, new[] { "foreign" }));
        }
    }
}
