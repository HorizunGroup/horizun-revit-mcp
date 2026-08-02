// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// What a script returns must survive the trip.
//
// Measured: a diagnostic assigned a dict to __output__ and the caller received
// the string "IronPython.Runtime.PythonDictionary". Not truncated, not an error -
// the payload replaced by the name of its own type, with executed=true beside it.
//
// IronPython's containers are ordinary IDictionary/IEnumerable implementations,
// so the rule is exercised here with plain .NET types and no IronPython in the
// test project.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScriptOutputTests
    {
        [Fact]
        public void A_dictionary_comes_back_as_data_not_as_its_type_name()
        {
            // THE REGRESSION.
            var value = new Dictionary<object, object>
            {
                { "log_path", @"C:\logs\revit-2026.log" },
                { "size_before", 1616 }
            };

            var r = ScriptOutput.Render(value);

            Assert.Equal("structure", r.Kind);
            Assert.False(r.Lossy);
            Assert.Equal(@"C:\logs\revit-2026.log", (string)r.Value["log_path"]);
            Assert.Equal(1616, (int)r.Value["size_before"]);
            Assert.DoesNotContain("PythonDictionary", r.Value.ToString());
        }

        [Fact]
        public void A_nested_structure_keeps_its_shape()
        {
            var value = new Dictionary<object, object>
            {
                { "items", new List<object> { 1, 2, 3 } },
                { "inner", new Dictionary<object, object> { { "ok", true } } }
            };

            var r = ScriptOutput.Render(value);

            Assert.Equal("structure", r.Kind);
            Assert.Equal(3, ((JArray)r.Value["items"]).Count);
            Assert.True((bool)r.Value["inner"]["ok"]);
        }

        [Fact]
        public void A_list_is_a_structure_too()
        {
            var r = ScriptOutput.Render(new List<object> { "a", "b" });

            Assert.Equal("structure", r.Kind);
            Assert.Equal(JTokenType.Array, r.Value.Type);
        }

        [Fact]
        public void Scalars_pass_through_with_their_type_intact()
        {
            Assert.Equal(JTokenType.String, ScriptOutput.Render("hola").Value.Type);
            Assert.Equal(JTokenType.Integer, ScriptOutput.Render(42).Value.Type);
            Assert.Equal(JTokenType.Float, ScriptOutput.Render(1.5).Value.Type);
            Assert.Equal(JTokenType.Boolean, ScriptOutput.Render(true).Value.Type);

            Assert.Equal("scalar", ScriptOutput.Render("hola").Kind);
            Assert.Equal("hola", (string)ScriptOutput.Render("hola").Value);
        }

        [Fact]
        public void A_string_that_looks_like_json_is_still_a_string()
        {
            // A script that already did json.dumps must not have its output double-parsed.
            var r = ScriptOutput.Render("{\"a\":1}");

            Assert.Equal("scalar", r.Kind);
            Assert.Equal(JTokenType.String, r.Value.Type);
            Assert.Equal("{\"a\":1}", (string)r.Value);
        }

        [Fact]
        public void No_output_is_absent_not_an_empty_string()
        {
            var r = ScriptOutput.Render(null);

            Assert.Equal("absent", r.Kind);
            Assert.Equal(JTokenType.Null, r.Value.Type);
            Assert.False(r.Lossy);
        }

        [Fact]
        public void A_value_that_cannot_be_serialized_says_so_instead_of_pretending()
        {
            var r = ScriptOutput.Render(new Unserializable());

            Assert.Equal("text_only", r.Kind);
            Assert.True(r.Lossy);
            Assert.NotNull(r.Note);
            Assert.Contains("LOST", r.Note);
            Assert.Contains("json.dumps", r.Note);
        }

        [Fact]
        public void A_ToString_that_throws_does_not_take_the_command_down()
        {
            var r = ScriptOutput.Render(new ExplodingToString());

            Assert.Equal("text_only", r.Kind);
            Assert.Contains("ToString() threw", (string)r.Value);
        }

        // ---- how big any of this may get ---------------------------------------

        [Fact]
        public void Printed_output_under_the_limit_is_returned_untouched()
        {
            string text = new string('a', 1000);

            Assert.Same(text, ScriptOutput.Clamp(text));
            Assert.Null(ScriptOutput.Clamp(null));
        }

        [Fact]
        public void Printed_output_over_the_limit_is_cut_and_the_cut_is_declared_in_the_text()
        {
            // The marker is IN the text, not a flag beside it. A caller reading the tail of
            // a script's log to see whether the loop finished is exactly who gets misled by
            // a silent truncation, and exactly who will not read a separate boolean.
            string text = new string('a', 500) + "THE-END";
            string clamped = ScriptOutput.Clamp(text, 100);

            Assert.StartsWith(new string('a', 100), clamped);
            Assert.DoesNotContain("THE-END", clamped);
            Assert.Contains("TRUNCATED", clamped);
            Assert.Contains("507 characters", clamped);   // the true length, not the limit
        }

        [Fact]
        public void The_text_limit_is_the_one_both_halves_agreed_on()
        {
            Assert.Equal(256 * 1024, Horizun.Contracts.Contract.MaxScriptTextChars);
        }

        /// <summary>
        /// A structure too big is REFUSED, not trimmed. Half a list is a valid list, and a
        /// caller that iterates it gets a confident, complete-looking, wrong answer - the
        /// one failure shape this whole file exists to prevent.
        /// </summary>
        [Fact]
        public void An_oversized_structure_comes_back_as_a_note_and_no_data_at_all()
        {
            var big = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 40000; i++) big.Add("element-" + i + "-with-some-padding-to-make-it-long");

            var r = ScriptOutput.Render(big);

            Assert.Equal("too_large", r.Kind);
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Null, r.Value.Type);
            Assert.Contains("NONE of it is returned", r.Note);
            Assert.Contains("not truncated", r.Note);
        }

        [Fact]
        public void A_structure_within_the_limit_still_comes_back_whole()
        {
            var d = new System.Collections.Generic.Dictionary<string, object> { ["walls"] = 42 };

            var r = ScriptOutput.Render(d);

            Assert.Equal("structure", r.Kind);
            Assert.Equal(42, (int)r.Value["walls"]);
        }

        private sealed class Unserializable
        {
            // Newtonsoft refuses a self-referencing loop, which is the realistic way a
            // script's object graph fails to serialize.
            public Unserializable Self { get; set; }
            public Unserializable() { Self = this; }
        }

        /// <summary>
        /// Both halves are needed to reach the fallback: the self-reference makes the JSON
        /// serializer refuse, and only THEN is ToString() called. An earlier version of this
        /// test had the throwing ToString() alone - Newtonsoft happily serialized it as {}
        /// and the fallback was never exercised.
        /// </summary>
        private sealed class ExplodingToString
        {
            public ExplodingToString Self { get; set; }
            public ExplodingToString() { Self = this; }
            public override string ToString() => throw new System.InvalidOperationException("no");
        }
    }
}
