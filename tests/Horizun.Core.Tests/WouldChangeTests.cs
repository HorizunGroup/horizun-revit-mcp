// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WouldChange (story 5.14): whether a planned write would move the parameter,
// as a tri-state whose third value is the point. The mistakes pinned here are
// the two ways a boolean would lie: an unreadable before-value collapsing into
// "it matches" (unknown must never compare equal), and a unit-aware string
// being judged against a number Revit has not parsed yet.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class WouldChangeTests
    {
        private static JObject Before(string storage, JToken value)
        {
            return new JObject { ["readable"] = true, ["storage"] = storage, ["value"] = value };
        }

        // ---------- the honest false: already holds the value ----------

        [Fact]
        public void A_string_already_holding_the_requested_value_is_false()
        {
            string why;
            var v = WouldChange.Judge("String", "D0XX-A1", Before("String", "D0XX-A1"), out why);
            Assert.False(v);
        }

        [Fact]
        public void A_double_within_relative_tolerance_is_false_not_drift()
        {
            string why;
            var v = WouldChange.Judge("Double", 0.15, Before("Double", 0.15 + 1e-15), out why);
            Assert.False(v);
        }

        [Fact]
        public void A_boolean_request_compares_against_the_stored_int()
        {
            string why;
            Assert.False(WouldChange.Judge("Integer", true, Before("Integer", 1), out why));
            Assert.True(WouldChange.Judge("Integer", true, Before("Integer", 0), out why).Value);
        }

        [Fact]
        public void An_elementid_already_pointing_there_is_false_and_null_means_clear()
        {
            string why;
            Assert.False(WouldChange.Judge("ElementId", 123456, Before("ElementId", "123456"), out why));
            // null requested coerces to -1, Revit's own 'no element'
            Assert.False(WouldChange.Judge("ElementId", JValue.CreateNull(), Before("ElementId", "-1"), out why));
            Assert.True(WouldChange.Judge("ElementId", 456, Before("ElementId", "123456"), out why).Value);
        }

        // ---------- the honest true: it would move ----------

        [Fact]
        public void A_different_value_is_true_and_an_empty_parameter_receiving_one_is_true()
        {
            string why;
            Assert.True(WouldChange.Judge("String", "new", Before("String", "old"), out why).Value);
            Assert.True(WouldChange.Judge("Double", 2.5, Before("Double", JValue.CreateNull()), out why).Value);
            Assert.True(WouldChange.Judge("Integer", 5, Before("Integer", JValue.CreateNull()), out why).Value);
        }

        // ---------- the honest null: it cannot be told ----------

        [Fact]
        public void An_unreadable_before_value_is_null_never_false()
        {
            string why;
            var unreadable = new JObject { ["readable"] = false, ["error"] = "Could not read this parameter: boom." };
            var v = WouldChange.Judge("String", "anything", unreadable, out why);
            Assert.Null(v);
            Assert.Contains("could not be read", why);
        }

        [Fact]
        public void A_missing_before_capture_is_null()
        {
            string why;
            Assert.Null(WouldChange.Judge("String", "x", null, out why));
            Assert.NotNull(why);
        }

        // The big case from the field: "15 cm" onto Double storage goes through
        // SetValueString, whose parse happens inside Revit at apply time. No
        // comparison made before the write can know what it will store.
        [Fact]
        public void A_unit_aware_string_onto_double_or_integer_storage_is_null()
        {
            string why;
            Assert.Null(WouldChange.Judge("Double", "15 cm", Before("Double", 0.492125), out why));
            Assert.Contains("unit-aware", why);
            Assert.Null(WouldChange.Judge("Integer", "3", Before("Integer", 3), out why));
            Assert.Contains("unit-aware", why);
        }

        // A request the apply will refuse gets no verdict here - null, with the
        // refusal named, never a guessed true or false.
        [Fact]
        public void A_request_the_apply_will_refuse_is_null_not_a_verdict()
        {
            string why;
            Assert.Null(WouldChange.Judge("String", JValue.CreateNull(), Before("String", "x"), out why));
            Assert.Contains("refuse", why);
            Assert.Null(WouldChange.Judge("Integer", 2.5, Before("Integer", 2), out why));
            Assert.Contains("refuse", why);
            Assert.Null(WouldChange.Judge("Double", true, Before("Double", 1.0), out why));
            Assert.Contains("refuse", why);
            Assert.Null(WouldChange.Judge("ElementId", "not-an-id", Before("ElementId", "123"), out why));
            Assert.Contains("refuse", why);
        }

        [Fact]
        public void A_storage_mismatch_between_row_and_capture_is_null()
        {
            string why;
            Assert.Null(WouldChange.Judge("Double", 1.0, Before("String", "1.0"), out why));
            Assert.Contains("storage", why);
        }

        [Fact]
        public void Null_and_empty_string_are_distinct_states()
        {
            string why;
            // an empty parameter receiving "" is still a write Revit may record
            Assert.True(WouldChange.Judge("String", "", Before("String", JValue.CreateNull()), out why).Value);
            Assert.False(WouldChange.Judge("String", "", Before("String", ""), out why));
        }
    }
}
