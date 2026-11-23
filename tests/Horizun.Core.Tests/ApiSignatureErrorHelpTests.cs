using Horizun.Revit.Core;
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ApiSignatureErrorHelpTests
    {
        [Fact]
        public void Logged_nonphysical_connector_error_does_not_suggest_silently_discarding_other_types()
        {
            var s = ScriptErrorSummary.Build("Exception: Connection status is available only for connectors of PhysicalConn type.", "pass", null);
            string hint = (string)s["hint"];
            Assert.Contains("only if", hint);
            Assert.Contains("other connector types", hint);
            Assert.Contains("horizun_api_help", hint);
        }
        // The error text below is anonymized from local logs. This fixture checks
        // the matching path only; it is not evidence of any Revit API version.
        public class FamilyInstance { public int LevelId { get; set; } }
        public class OverloadFixture { public void Delete(int id) { } public void Delete(string id) { } }
        public class ElementId { public ElementId(int id) { } public ElementId(long id) { } }
        public enum BuiltInCategory { OST_Walls, OST_Doors }

        [Fact]
        public void Logged_expected_element_id_error_offers_loaded_constructors_without_guessing_the_failed_member()
        {
            var s = ScriptErrorSummary.Build("TypeError: expected ElementId, got Int32", "pass", null);
            ScriptErrorSummary.AttachExpectedTypeApiHelp(s, new List<Type> { typeof(ElementId) }, "fixture");
            Assert.Equal(2, (int)s["expected_api_type"]["constructors_total"]);
            Assert.Contains("ElementId(int", (string)s["hint"]);
            Assert.Contains("ElementId(long", (string)s["hint"]);
            Assert.Contains("failing member", (string)s["hint"]);
            Assert.Contains("horizun_api_help", (string)s["hint"]);
            var ambiguous = ScriptErrorSummary.Build("TypeError: expected ElementId, got Int32", "pass", null);
            ScriptErrorSummary.AttachExpectedTypeApiHelp(ambiguous, new List<Type> { typeof(ElementId), typeof(ElementId) }, "fixture");
            Assert.Null(ambiguous["expected_api_type"]);
            var nonApi = ScriptErrorSummary.Build("TypeError: expected object, got NoneType", "pass", null);
            ScriptErrorSummary.AttachExpectedTypeApiHelp(nonApi, new List<Type> { typeof(ElementId) }, "fixture");
            Assert.Null(nonApi["expected_api_type"]);
        }

        [Fact]
        public void Logged_type_attribute_error_suggests_enum_value_only_with_an_identifiable_source_line()
        {
            string traceback = "Traceback (most recent call last):\n  File \"<string>\", line 2, in <module>\nAttributeError: 'type' object has no attribute 'OST_walls'";
            string source = "pass\ncategory = BuiltInCategory.OST_walls";
            var s = ScriptErrorSummary.Build(traceback, source, null);
            ScriptErrorSummary.AttachEnumApiHelp(s, source, new List<Type> { typeof(BuiltInCategory) }, "fixture");
            Assert.Contains("OST_Walls", s["api_suggestion"]["closest_members"].ToString());
            Assert.Contains("does not establish the runtime receiver", (string)s["hint"]);
            Assert.Contains("horizun_api_help", (string)s["hint"]);
            var ambiguous = ScriptErrorSummary.Build(traceback, source, null);
            ScriptErrorSummary.AttachEnumApiHelp(ambiguous, source, new List<Type> { typeof(BuiltInCategory), typeof(BuiltInCategory) }, "fixture");
            Assert.Null(ambiguous["api_suggestion"]);
            var noSource = ScriptErrorSummary.Build(traceback, "pass", null);
            ScriptErrorSummary.AttachEnumApiHelp(noSource, "pass", new List<Type> { typeof(BuiltInCategory) }, "fixture");
            Assert.Null(noSource["api_suggestion"]);
        }

        [Fact]
        public void Logged_missing_api_name_offers_only_a_candidate_and_preserves_variable_uncertainty()
        {
            var s = ScriptErrorSummary.Build("NameError: name 'BuiltInParameter' is not defined", "pass", null);
            ScriptErrorSummary.AttachNameApiHelp(s, new List<Type> { typeof(BuiltInParameter) }, "fixture");
            Assert.Contains("BuiltInParameter", s["api_name_candidates"]["type_names"].ToString());
            Assert.Contains("may instead be a script variable", (string)s["api_name_candidates"]["basis"]);
            Assert.Contains("horizun_api_help", (string)s["hint"]);
            var variable = ScriptErrorSummary.Build("NameError: name 'arguments' is not defined", "pass", null);
            ScriptErrorSummary.AttachNameApiHelp(variable, new List<Type> { typeof(BuiltInParameter) }, "fixture");
            Assert.Null(variable["api_name_candidates"]);
        }
        public class BuiltInParameter { }

        [Fact]
        public void Misspelled_missing_name_offers_approximate_api_types_without_asserting_intent()
        {
            var s = ScriptErrorSummary.Build("NameError: name 'BuiltInParametr' is not defined", "pass", null);
            ScriptErrorSummary.AttachNameApiHelp(s, new List<Type> { typeof(BuiltInParameter) }, "fixture");
            Assert.Contains("BuiltInParameter", s["api_name_candidates"]["type_names"].ToString());
            Assert.Contains("approximate", (string)s["api_name_candidates"]["basis"]);
            Assert.Contains("may instead be a script variable", (string)s["api_name_candidates"]["basis"]);
            Assert.Contains("horizun_api_help", (string)s["hint"]);
        }

        [Fact]
        public void Explicit_member_gets_only_its_reflected_overloads()
        {
            var s = ScriptErrorSummary.Build("  at Autodesk.Revit.DB.Document.Delete(ElementId elementId)\nTypeError: expected ElementId, got int", "pass", null);
            s["signature_check"]["arguments"]["type_name"] = typeof(OverloadFixture).FullName;
            ScriptErrorSummary.AttachSignatureApiHelp(s, new List<Type> { typeof(OverloadFixture) }, "fixture");
            Assert.Equal("fixture", (string)s["signature_check"]["revit_version"]);
            Assert.Equal(2, ((JArray)s["signature_check"]["reflected_overloads"]).Count);
            Assert.Contains("Delete(int", (string)s["hint"]);
            Assert.Contains("Delete(string", (string)s["hint"]);
            Assert.Contains("horizun_api_help", (string)s["hint"]);
        }

        [Fact]
        public void Logged_family_instance_attribute_error_uses_reflected_fixture_without_asserting_identity()
        {
            var s = ScriptErrorSummary.Build("AttributeError: 'FamilyInstance' object has no attribute 'Level'", "pass", null);
            ScriptErrorSummary.AttachAttributeApiHelp(s, new List<Type> { typeof(FamilyInstance) }, "fixture");
            Assert.Contains("LevelId", s["api_suggestion"]["closest_members"].ToString());
            Assert.Contains("verify the receiver type", (string)s["hint"]);
        }

        [Fact]
        public void Attribute_error_offers_reflected_candidates_without_claiming_receiver_identity()
        {
            var s = ScriptErrorSummary.Build("AttributeError: 'JObject' object has no attribute 'Parze'", "pass", null);
            ScriptErrorSummary.AttachAttributeApiHelp(s, new List<Type> { typeof(JObject) }, "test-version");
            Assert.Equal("Newtonsoft.Json.Linq.JObject", (string)s["api_suggestion"]["type_name"]);
            Assert.Equal("test-version", (string)s["api_suggestion"]["revit_version"]);
            Assert.Contains("Parse(string", s["api_suggestion"]["closest_members"].ToString());
            Assert.Contains("verify the receiver type", ScriptErrorSummary.Render(s));
            Assert.Contains("horizun_api_help", ScriptErrorSummary.Render(s));

            var wrongCase = ScriptErrorSummary.Build("AttributeError: 'JObject' object has no attribute 'parse'", "pass", null);
            ScriptErrorSummary.AttachAttributeApiHelp(wrongCase, new List<Type> { typeof(JObject) }, "test-version");
            Assert.Contains("Parse(string", wrongCase["api_suggestion"]["closest_members"].ToString());

            var ambiguous = ScriptErrorSummary.Build("AttributeError: 'JObject' object has no attribute 'Parze'", "pass", null);
            ScriptErrorSummary.AttachAttributeApiHelp(ambiguous, new List<Type> { typeof(JObject), typeof(JObject) }, "test-version");
            Assert.Null(ambiguous["api_suggestion"]);
        }
        [Theory]
        [InlineData("TypeError")]
        [InlineData("Autodesk.Revit.Exceptions.ArgumentException")]
        [InlineData("ArgumentOutOfRangeException")]
        [InlineData("ArgumentsInconsistentException")]
        [InlineData("System.Reflection.AmbiguousMatchException")]
        [InlineData("MissingMethodException")]
        [InlineData("InvalidCastException")]
        public void Overload_families_always_name_the_read_only_signature_check(string exception)
        {
            var s = ScriptErrorSummary.Build(exception + ": expected ICollection[ElementId], got list", "pass", null);
            Assert.Contains("check_signature", (string)s["hint"]);
            Assert.Contains("CLR", (string)s["hint"]);
            Assert.Null(s["signature_check"]);
            Assert.Contains("List[ElementId]", (string)s["hint"]);
        }

        [Fact]
        public void Explicit_clr_constructor_frame_and_actual_clr_type_give_an_executable_request()
        {
            var s = ScriptErrorSummary.Build("Traceback (most recent call last):\n  at Autodesk.Revit.DB.ElementId..ctor(Int64 value)\nTypeError: expected System.Int64, got System.Int32", "pass", null);
            var request = (JObject)s["signature_check"]["arguments"];
            Assert.Equal("check_signature", (string)request["operation"]);
            Assert.Equal("Autodesk.Revit.DB.ElementId", (string)request["type_name"]);
            Assert.Equal(".ctor", (string)request["member_filter"]);
            Assert.Equal("System.Int32", (string)request["argument_types"][0]);
            Assert.False((bool)s["signature_check"]["argument_types_required"]);
            Assert.Contains("member_filter=\".ctor\"", ScriptErrorSummary.Render(s));
        }

        [Fact]
        public void Method_frame_identifies_member_but_never_invents_types_from_python_int()
        {
            var s = ScriptErrorSummary.Build("  at Autodesk.Revit.DB.Document.Delete(ElementId elementId)\nTypeError: expected ElementId, got int", "doc.Delete(1)", null);
            Assert.Equal("Delete", (string)s["signature_check"]["arguments"]["member_filter"]);
            Assert.True((bool)s["signature_check"]["argument_types_required"]);
            Assert.Null(s["signature_check"]["arguments"]["argument_types"]);
            Assert.Contains("CLR_TYPES", (string)s["hint"]);
            Assert.Contains("ElementId", (string)s["hint"]);
        }

        [Fact]
        public void Ambiguous_traceback_members_and_untyped_receivers_do_not_get_an_invented_target()
        {
            var s = ScriptErrorSummary.Build("  at Autodesk.Revit.DB.Document.Delete(ElementId id)\n  at Autodesk.Revit.DB.ElementId..ctor(Int64 id)\nTypeError: multiple targets", "doc.Delete(ElementId(1))", null);
            Assert.Null(s["signature_check"]);
            Assert.Contains("check_signature", (string)s["hint"]);
            s = ScriptErrorSummary.Build("TypeError: Multiple targets could match", "custom.Delete(1)", null);
            Assert.Null(s["signature_check"]);
        }

        [Fact]
        public void Non_overload_errors_keep_existing_help()
        {
            var s = ScriptErrorSummary.Build("AttributeError: 'NoneType' object has no attribute 'Name'", "pass", null);
            Assert.DoesNotContain("check_signature", (string)s["hint"]);
            Assert.Contains("returned None", (string)s["hint"]);
        }

        [Fact]
        public void Echoed_code_and_multi_argument_frames_do_not_invent_a_complete_request()
        {
            var echoed = ScriptErrorSummary.Build("TypeError: failed at Autodesk.Revit.DB.Document.Delete(ElementId id)", "pass", null);
            Assert.Null(echoed["signature_check"]);
            var multi = ScriptErrorSummary.Build("  at Autodesk.Revit.DB.SomeType.Call(Int64 first, Int64 second)\nTypeError: expected System.Int64, got System.Int32", "pass", null);
            Assert.NotNull(multi["signature_check"]);
            Assert.True((bool)multi["signature_check"]["argument_types_required"]);
            Assert.Null(multi["signature_check"]["arguments"]["argument_types"]);
        }

        [Theory]
        [InlineData("  at Autodesk.Revit.DB.SomeType.Call(Int64 value)\nTypeError: expected System.Int64, got System.String; expected System.Int64, got System.Int32")]
        [InlineData("  at Autodesk.Revit.DB.SomeType.Call(Int64 value)\n  at Autodesk.Revit.DB.SomeType.Call(Int64 first, Int64 second)\nTypeError: expected System.Int64, got System.Int32")]
        public void Conflicting_type_clauses_or_frame_arities_leave_actual_arguments_unknown(string traceback)
        {
            var s = ScriptErrorSummary.Build(traceback, "pass", null);
            Assert.NotNull(s["signature_check"]);
            Assert.True((bool)s["signature_check"]["argument_types_required"]);
            Assert.Null(s["signature_check"]["arguments"]["argument_types"]);
        }
    }
}
