// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE SAME QUESTION GOT TWO OPPOSITE WRONG ANSWERS.
//
// "Did the values a rule declared actually land?" was first answered by reading
// keys the parameter writer does not emit, so null became zero and every
// successful write was reported as a failure. The correction then overshot: it
// demanded that every row be re-read as the exact value passed, which sounds
// stricter and is not - a unit string on a numeric parameter ("900 mm" on a sill
// height, "60" on a Double fire rating, which is the example in this bridge's own
// documentation) is applied through SetValueString and can only ever be confirmed
// by parsing the read-back. It lands. It is in the model. And it was counted as
// not written, which stopped the conversion and told somebody their correctly
// annotated elements carry none of the values they can see.
//
// Neither mistake was reproducible without a Revit, which is exactly why the
// arithmetic is here now.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadParameterOutcomeTests
    {
        /// <summary>The shape horizun_write_params_verified actually returns.</summary>
        private static JObject Writer(int exact = 0, int byParse = 0, int failed = 0,
                                      int unknown = 0, int unresolved = 0)
        {
            return new JObject
            {
                ["writes_confirmed_against_your_value"] = exact,
                ["writes_confirmed_by_parse_read_back_only"] = byParse,
                ["failed"] = failed,
                ["unknown"] = unknown,
                ["unresolved"] = unresolved
            };
        }

        // ------------------------------------------------------------- it landed

        [Fact]
        public void Every_row_re_read_as_the_exact_value_is_written()
        {
            JObject o = CadParameterOutcome.Summarise(Writer(exact: 2), 2);
            Assert.True((bool)o["all_written"]);
            Assert.Equal(2, (int)o["landed"]);
        }

        [Fact]
        public void A_UNIT_STRING_on_a_numeric_parameter_is_written_too()
        {
            // THE DEFECT THIS FILE EXISTS FOR. The writer can only confirm this
            // one by parsing what it read back, and that is not a reason to tell
            // somebody the value is missing from a model that holds it.
            JObject o = CadParameterOutcome.Summarise(Writer(byParse: 1), 1);

            Assert.True((bool)o["all_written"]);
            Assert.Equal(1, (int)o["confirmed_by_parse_read_back_only"]);
            Assert.Equal(0, (int)o["confirmed_against_your_value"]);
        }

        [Fact]
        public void A_mixture_of_both_kinds_of_evidence_is_still_written()
        {
            JObject o = CadParameterOutcome.Summarise(Writer(exact: 1, byParse: 1), 2);
            Assert.True((bool)o["all_written"]);
        }

        [Fact]
        public void But_HOW_it_was_confirmed_is_still_reported_separately()
        {
            // Reported, never folded in: "written, and read back as Revit formats
            // it" and "not written" are different things to tell somebody, and
            // only one of them is a reason to stop.
            JObject o = CadParameterOutcome.Summarise(Writer(exact: 1, byParse: 1), 2);
            Assert.Equal(1, (int)o["confirmed_against_your_value"]);
            Assert.Equal(1, (int)o["confirmed_by_parse_read_back_only"]);
            Assert.NotNull((string)o["strength_means"]);
        }

        // --------------------------------------------------------- it did not land

        [Fact]
        public void A_row_that_FAILED_is_not_written()
        {
            JObject o = CadParameterOutcome.Summarise(Writer(exact: 1, failed: 1), 2);
            Assert.False((bool)o["all_written"]);
            Assert.Equal(1, (int)o["failed"]);
        }

        [Fact]
        public void A_row_the_writer_could_not_RE_READ_is_not_written_either()
        {
            // unknown means the writer set something and could not read it back.
            // Treating that as success is precisely the claim this bridge exists
            // not to make.
            JObject o = CadParameterOutcome.Summarise(Writer(exact: 1, unknown: 1), 2);
            Assert.False((bool)o["all_written"]);
        }

        [Fact]
        public void A_row_that_resolved_to_no_element_is_not_written()
        {
            JObject o = CadParameterOutcome.Summarise(Writer(exact: 1, unresolved: 1), 2);
            Assert.False((bool)o["all_written"]);
        }

        [Fact]
        public void FEWER_rows_landing_than_were_asked_for_is_not_written()
        {
            // Nothing failed and nothing is unknown, and one row simply never
            // appears in any bucket. Silence is not success.
            JObject o = CadParameterOutcome.Summarise(Writer(exact: 1), 2);
            Assert.False((bool)o["all_written"]);
        }

        [Fact]
        public void A_writer_that_answered_NOTHING_is_not_written()
        {
            JObject o = CadParameterOutcome.Summarise(null, 2);
            Assert.False((bool)o["all_written"]);
            Assert.Equal(0, (int)o["landed"]);
        }

        [Fact]
        public void A_writer_reply_MISSING_the_keys_reports_nothing_landed_rather_than_everything()
        {
            // The first version of this read data["verified"] and data["written"],
            // which the writer does not emit; both came back null, null became
            // zero, and the comparison then failed every successful write. An
            // absent key must read as zero landed, never as agreement.
            JObject o = CadParameterOutcome.Summarise(new JObject { ["mode"] = "atomic" }, 2);
            Assert.False((bool)o["all_written"]);
            Assert.Equal(0, (int)o["landed"]);
        }

        [Fact]
        public void The_denominator_is_what_was_DECLARED_and_not_what_was_sent()
        {
            // The mirror of the first defect. all_written was measured against the
            // writes that survived the skip loop, so a declaration that fell out on
            // the way quietly shrank the denominator - and a partial loss produced
            // exactly the same verdict as a clean stage. Two declared, one sent,
            // one landed is NOT everything written.
            JObject o = CadParameterOutcome.Summarise(Writer(exact: 1), 2);
            Assert.False((bool)o["all_written"]);
            Assert.Equal(1, (int)o["landed"]);
        }

        // ------------------------------------------------------------ the rehearsal

        [Fact]
        public void A_rehearsal_says_it_rehearsed_NOTHING_rather_than_claiming_success()
        {
            // In a dry run the elements do not exist, so there is no id to write a
            // parameter against and the writer is never called. This used to
            // answer all_written: true - a rehearsal reporting success over
            // something it never looked at.
            var declared = new JArray(new JObject
            {
                ["element_index"] = 0,
                ["parameter"] = "Fire Rating",
                ["value"] = "60",
                ["scope"] = "instance"
            });

            JObject o = CadParameterOutcome.NotRehearsed(declared, 1);

            Assert.False((bool)o["rehearsed"]);
            Assert.Equal(JTokenType.Null, o["all_written"].Type);
            Assert.Contains("NOTHING WAS REHEARSED", (string)o["why"]);
        }

        [Fact]
        public void And_it_still_shows_WHAT_would_be_written()
        {
            // A rehearsal that measured nothing can at least say what it was
            // going to do, which is the only useful thing left to report.
            var declared = new JArray(new JObject
            {
                ["element_index"] = 0,
                ["parameter"] = "Fire Rating",
                ["value"] = "60",
                ["scope"] = "type"
            });

            JObject o = CadParameterOutcome.NotRehearsed(declared, 1);

            Assert.Equal(1, (int)o["declared_count"]);
            Assert.Equal("Fire Rating", (string)((JArray)o["declared"])[0]["parameter"]);
            Assert.Equal("type", (string)((JArray)o["declared"])[0]["scope"]);
        }

        [Fact]
        public void all_written_is_NULL_and_not_false_when_nothing_was_measured()
        {
            // False would be a claim too: it would say the parameters did not
            // land, and nothing here looked. Neither answer is available.
            JObject o = CadParameterOutcome.NotRehearsed(new JArray(), 0);
            Assert.Equal(JTokenType.Null, o["all_written"].Type);
        }
    }
}
