// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A QUANTITY READ THAT FAILS, END TO END, WITHOUT REVIT.
//
// The live harness reported "a link whose elements cannot be READ" as a missing
// fixture, and it is right that an element whose geometry Revit itself cannot
// evaluate cannot be built on demand. What CAN be exercised deterministically is
// everything the failure travels through, by substituting the one thing Revit
// produces: a Measurement in the Failed state.
//
// WHAT THESE TESTS GUARANTEE:
//   * a failed read becomes `unreadable`, never `absent` and never a zero;
//   * the reading keeps its element's identity - document, link instance, id -
//     so a lower bound can be traced to the element that caused it;
//   * a measured ZERO stays a number, never an absence.
//
// What the COMPARISON does with an unreadable reading - not_comparable,
// incomplete_read, a known_total that is a lower bound - is proved next door in
// BudgetComparisonRulesTests and is not repeated here.
//
// WHAT THEY DO NOT GUARANTEE: that Revit's own geometry read throws for a
// particular corrupt element. That needs such an element, and this machine has
// none; the harness says so by name rather than simulating one.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class TakeoffUnreadableReadingTests
    {
        [Fact]
        public void A_read_that_FAILED_is_unreadable_and_a_read_that_does_not_apply_is_absent()
        {
            Assert.Equal(QuantityState.Unreadable,
                         TakeoffReadingRules.StateFor(Measurement.Failed("the geometry read threw")));
            Assert.Equal(QuantityState.Absent,
                         TakeoffReadingRules.StateFor(Measurement.NotApplicable("this element has no location line")));
            Assert.Equal(QuantityState.Measured, TakeoffReadingRules.StateFor(Measurement.Of(0.0)));

            // A measured ZERO is a number, not an absence. Reading it as one is the
            // mistake that prices an element out of a budget.
            Assert.False(TakeoffReadingRules.IsLowerBound(Measurement.Of(0.0)));
            Assert.True(TakeoffReadingRules.IsLowerBound(Measurement.Failed("threw")));
            Assert.False(TakeoffReadingRules.IsLowerBound(Measurement.NotApplicable("n/a")));

            // A missing measurement is unreadable, never quietly absent.
            Assert.Equal(QuantityState.Unreadable, TakeoffReadingRules.StateFor(null));
        }

        // ------------------------------------------------- and what it travels through

        private static JObject Reading(string state, double? value, string unit = "m3") =>
            new JObject
            {
                ["state"] = state, ["unit"] = unit,
                ["value"] = value.HasValue ? (JToken)value.Value : JValue.CreateNull(),
                ["reason"] = state == QuantityState.Unreadable ? "the geometry read threw" : null
            };

        private static JObject Row(string id, string code, string document, string linkInstance, JObject volume) =>
            new JObject
            {
                ["element_id"] = id, ["classification_code"] = code, ["document"] = document,
                ["link_instance_id"] = linkInstance,
                ["quantities"] = new JObject { ["volume"] = volume }
            };

        [Fact]
        public void An_unreadable_reading_keeps_the_identity_of_the_element_that_caused_it()
        {
            var rows = new JArray(
                Row("11", "A-1", "Link.rvt", "5000", Reading(QuantityState.Unreadable, null)),
                Row("11", "A-1", "Host.rvt", null, Reading(QuantityState.Measured, 2.0)));

            string problem;
            var read = BudgetComparisonRules.ReadModelRows(rows, "classification_code", out problem);
            Assert.Null(problem);
            Assert.Equal(2, read.Count);

            // The same element id in two documents is two rows, and the unreadable
            // one names the placement it came from.
            var bad = read.Single(r => r.Quantities["volume"].State == QuantityState.Unreadable);
            Assert.Equal("11", bad.ElementId);
            Assert.Equal("Link.rvt", bad.Document);
            Assert.Equal("5000", bad.LinkInstanceId);
            Assert.Equal("the geometry read threw", bad.Quantities["volume"].Reason);
            Assert.Null(bad.Quantities["volume"].Value);
        }

        [Fact]
        public void A_measured_ZERO_is_a_number_and_an_unreadable_read_is_not()
        {
            // The two answers a total must never confuse. BudgetComparisonRulesTests
            // already proves what the COMPARISON does with an unreadable reading -
            // not_comparable, incomplete_read, a known_total that is a LOWER BOUND -
            // and this is the step before it: which measurement becomes which state.
            var rows = new JArray(
                Row("21", "A-1", "Host.rvt", null, Reading(QuantityState.Measured, 0.0)),
                Row("22", "A-1", "Host.rvt", null, Reading(QuantityState.Unreadable, null)));

            string problem;
            var read = BudgetComparisonRules.ReadModelRows(rows, "classification_code", out problem);
            Assert.Null(problem);

            var zero = read.Single(r => r.ElementId == "21").Quantities["volume"];
            var unreadable = read.Single(r => r.ElementId == "22").Quantities["volume"];
            Assert.Equal(QuantityState.Measured, zero.State);
            Assert.Equal(0.0, zero.Value);
            Assert.Equal(QuantityState.Unreadable, unreadable.State);
            Assert.Null(unreadable.Value);
        }
    }
}
