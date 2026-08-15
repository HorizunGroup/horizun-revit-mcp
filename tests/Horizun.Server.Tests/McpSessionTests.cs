// -----------------------------------------------------------------------------
// Horizun Server tests - MCP request ids and lifecycle, without starting Revit.
// -----------------------------------------------------------------------------
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class McpSessionTests
    {
        private static JObject Message(JToken id = null)
        {
            var message = new JObject { ["jsonrpc"] = "2.0", ["method"] = "ping" };
            if (id != null) message["id"] = id;
            return message;
        }

        [Theory]
        [InlineData("null")]
        [InlineData("1.5")]
        [InlineData("{}")]
        [InlineData("[]")]
        public void Only_string_or_integer_request_ids_are_accepted(string json)
        {
            var session = new McpSession();
            JObject message = Message(JToken.Parse(json));

            Assert.False(session.TryAcceptId(message, out bool notification, out object id, out string error));
            Assert.False(notification);
            Assert.Null(id);
            Assert.Contains("string or integer", error);
        }

        [Fact]
        public void An_absent_id_is_a_notification_but_an_explicit_null_is_invalid()
        {
            var session = new McpSession();
            Assert.True(session.TryAcceptId(Message(), out bool notification, out _, out _));
            Assert.True(notification);

            JObject explicitNull = Message();
            explicitNull["id"] = JValue.CreateNull();
            Assert.False(session.TryAcceptId(explicitNull, out notification, out _, out _));
            Assert.False(notification);
        }

        [Fact]
        public void Numeric_and_string_ids_are_distinct_but_each_is_lifetime_unique()
        {
            var session = new McpSession();
            Assert.True(session.TryAcceptId(Message(1), out _, out _, out _));
            Assert.True(session.TryAcceptId(Message("1"), out _, out _, out _));

            Assert.False(session.TryAcceptId(Message(1), out _, out _, out string numericError));
            Assert.False(session.TryAcceptId(Message("1"), out _, out _, out string stringError));
            Assert.Contains("already used", numericError);
            Assert.Contains("already used", stringError);
        }

        [Fact]
        public void Initialize_request_must_be_the_first_interaction()
        {
            var session = new McpSession();
            Assert.False(session.Allows("tools/list", false, out string error));
            Assert.Contains("first interaction", error);
            Assert.False(session.Allows("initialize", true, out error));
            Assert.True(session.Allows("initialize", false, out error));
            Assert.Null(error);
        }

        [Fact]
        public void Operations_wait_for_the_initialized_notification()
        {
            var session = new McpSession();
            Assert.True(session.Allows("initialize", false, out _));
            session.InitializeAnswerDelivered();

            Assert.True(session.Allows("ping", false, out _));
            Assert.False(session.Allows("tools/list", false, out string error));
            Assert.Contains("notifications/initialized", error);
            Assert.False(session.Allows("notifications/initialized", false, out error));
            Assert.True(session.Allows("notifications/initialized", true, out _));

            session.InitializedNotificationAccepted();
            Assert.True(session.Allows("tools/list", false, out _));
            Assert.True(session.Allows("tools/call", false, out _));
        }

        [Fact]
        public void Initialize_and_initialized_cannot_repeat()
        {
            var session = new McpSession();
            session.InitializeAnswerDelivered();
            Assert.False(session.Allows("initialize", false, out _));
            session.InitializedNotificationAccepted();
            Assert.False(session.Allows("initialize", false, out _));
            Assert.False(session.Allows("notifications/initialized", true, out _));
        }
    }
}
