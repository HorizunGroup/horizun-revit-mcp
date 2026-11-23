using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // Field signals 2026-10: 10-20 % of execute_python failures were scripts written for
    // another Revit year or for IronPython 2. These pin what the bridge rewrites before
    // running, what it refuses with the correction, and that line numbers survive.
    public class PythonCompatTests
    {
        private static readonly string[] Categories = { "OST_Walls", "OST_Cornices", "OST_Floors", "OST_WallSweeps_Obsolete", "OST_Doors" };

        [Fact]
        public void IntegerValue_becomes_Value_on_2026_and_later_only()
        {
            string src = "x = e.Id.IntegerValue\nprint(x)\n";
            var r26 = PythonCompat.Apply(src, 2026, null);
            Assert.Equal("x = e.Id.Value\nprint(x)\n", r26.Code);
            Assert.Equal("element_id_integer_value", (string)r26.Rewrites[0]["rule"]);
            Assert.Equal(1, (int)r26.Rewrites[0]["line"]);

            Assert.Equal(src, PythonCompat.Apply(src, 2024, null).Code);
            Assert.Equal(src, PythonCompat.Apply(src, 2025, null).Code);
        }

        [Fact]
        public void Id_Value_becomes_IntegerValue_on_2023_for_names_ending_in_Id_only()
        {
            string src = "a = e.Id.Value\nb = v.ViewTemplateId.Value\nc = w.GetTypeId().Value\nd = e.UniqueId.Value\nf = p.Value\n";
            var r = PythonCompat.Apply(src, 2023, null);
            Assert.Equal("a = e.Id.IntegerValue\nb = v.ViewTemplateId.IntegerValue\nc = w.GetTypeId().IntegerValue\nd = e.UniqueId.Value\nf = p.Value\n", r.Code);
            Assert.Equal(3, r.Rewrites.Count);
            Assert.Equal(src, PythonCompat.Apply(src, 2024, null).Code);
        }

        [Fact]
        public void A_net_member_named_None_is_read_with_getattr()
        {
            string src = "ok = [p for p in ps if p.StorageType != StorageType.None]\nz = DB.StorageType.None\n";
            var r = PythonCompat.Apply(src, 2025, null);
            Assert.Equal("ok = [p for p in ps if p.StorageType != getattr(StorageType, 'None')]\nz = getattr(DB.StorageType, 'None')\n", r.Code);
            Assert.All(r.Rewrites, x => Assert.Equal("dotnet_member_named_none", (string)x["rule"]));
        }

        [Fact]
        public void Nothing_inside_strings_or_comments_is_rewritten()
        {
            string src = "s = 'e.Id.IntegerValue and StorageType.None'\n# StorageType.None .IntegerValue\nprint(s)\n";
            var r = PythonCompat.Apply(src, 2027, new[] { "OST_Walls" });
            Assert.Equal(src, r.Code);
            Assert.Empty(r.Rewrites);
        }

        [Fact]
        public void Unknown_category_is_refused_with_the_closest_real_names()
        {
            var r = PythonCompat.Apply("x = 1\ncats = [BuiltInCategory.OST_WallSweeps]\n", 2026, Categories);
            JObject e = (JObject)Assert.Single(r.Errors);
            Assert.Equal("unknown_builtincategory", (string)e["rule"]);
            Assert.Equal(2, (int)e["line"]);
            Assert.Contains("BuiltInCategory.OST_WallSweeps_Obsolete", e["suggestions"].Select(t => (string)t));
            Assert.StartsWith("BuiltInCategory.OST_WallSweeps does not exist", (string)e["fix"]);
        }

        [Fact]
        public void A_category_wrong_only_in_case_is_corrected()
        {
            var r = PythonCompat.Apply("c = BuiltInCategory.OST_walls\n", 2026, Categories);
            Assert.Empty(r.Errors);
            Assert.Equal("c = BuiltInCategory.OST_Walls\n", r.Code);
            Assert.Equal("builtincategory_case", (string)r.Rewrites[0]["rule"]);
        }

        [Fact]
        public void Transactions_are_tracked_and_closes_guarded_without_changing_line_numbers()
        {
            string src = "t = Transaction(doc, 'a')\ns = DB.SubTransaction(doc)\ng = TransactionGroup(doc)\ne = Transaction()\n" +
                         "class T(Transaction):\n    pass\ndef Transaction(x): pass\nfam.Close(False)\nf().Close()\n";
            var r = PythonCompat.Apply(src, 2026, null);
            Assert.Equal(
                "t = _hz_new_tx(Transaction, doc, 'a')\ns = _hz_new_tx(DB.SubTransaction, doc)\ng = _hz_new_tx(TransactionGroup, doc)\n" +
                "e = _hz_new_tx(Transaction)\nclass T(Transaction):\n    pass\ndef Transaction(x): pass\n_hz_closing(fam).Close(False)\nf().Close()\n",
                r.Code);
            Assert.Equal(4, r.TrackedTransactions);
            Assert.Equal(1, r.GuardedCloses);
            Assert.Equal(src.Count(c => c == '\n'), r.Code.Count(c => c == '\n'));
        }

        [Fact]
        public void Auto_transaction_only_for_scripts_that_write_and_manage_no_transaction_or_document()
        {
            Assert.Contains(".Delete(", PythonCompat.AutoTransactionMarkers("doc.Delete(ids)\n"));
            Assert.Contains(".Set(", PythonCompat.AutoTransactionMarkers("p.Set(3.0)\nv.Name = 'x'\n"));
            Assert.Contains(".Name=", PythonCompat.AutoTransactionMarkers("v.Name = 'x'\n"));
            Assert.Empty(PythonCompat.AutoTransactionMarkers("t = Transaction(doc, 'a')\nt.Start()\ndoc.Delete(ids)\nt.Commit()\n"));
            Assert.Empty(PythonCompat.AutoTransactionMarkers("with horizun.transaction('x'):\n    doc.Delete(ids)\n"));
            Assert.Empty(PythonCompat.AutoTransactionMarkers("d = app.OpenDocumentFile(p)\nd.Delete(ids)\n"));
            Assert.Empty(PythonCompat.AutoTransactionMarkers("print([e.Name for e in els])\nif v.Name == 'x': pass\n"));
            Assert.Empty(PythonCompat.AutoTransactionMarkers("# doc.Delete(ids)\ns = 'p.Set(1)'\n"));
        }

        [Fact]
        public void Closest_prefers_names_that_contain_the_typo()
        {
            List<string> c = PythonCompat.Closest("OST_Door", Categories, 2);
            Assert.Equal("OST_Doors", c[0]);
        }
    }

    public class ScriptErrorSummaryTests
    {
        private const string Code = "a = 1\nb = None\nc = b.Id\nd = 4\ne = 5\n";

        [Fact]
        public void Names_the_line_of_the_script_with_context_type_and_hint()
        {
            string tb = "Traceback (most recent call last):\n  File \"<string>\", line 3, in <module>\n" +
                        "AttributeError: 'NoneType' object has no attribute 'Id'\n";
            JObject s = ScriptErrorSummary.Build(tb, Code, null);
            Assert.Equal("AttributeError", (string)s["type"]);
            Assert.Equal(3, (int)s["line"]);
            Assert.Equal(">    3 | c = b.Id", (string)s["context"][2]);
            Assert.Equal(5, ((JArray)s["context"]).Count);
            Assert.Contains("returned None", (string)s["hint"]);
            string text = ScriptErrorSummary.Render(s);
            Assert.StartsWith("Python error at line 3 of the script: AttributeError: 'NoneType' object has no attribute 'Id'", text);
        }

        [Fact]
        public void Skips_the_bridge_prelude_frames_and_follows_a_code_path_file()
        {
            string tb = "Traceback (most recent call last):\n  File \"D:\\\\work\\\\s.py\", line 2, in <module>\n" +
                        "  File \"<string>\", line 140, in _hz_transaction\n" +
                        "Exception: The referenced object is not valid, possibly because it has been deleted from the database.\n";
            JObject s = ScriptErrorSummary.Build(tb.Replace("\\\\", "\\"), Code, @"D:\work\s.py");
            Assert.Equal(2, (int)s["line"]);
            Assert.Equal("Exception", (string)s["type"]);
            Assert.Contains("deleted", (string)s["hint"]);
        }

        [Fact]
        public void A_compile_error_names_its_line_too()
        {
            string tb = "  File \"<string>\", line 3\n    c = b.Id(\n         ^\nSyntaxError: unexpected EOF while parsing\n";
            JObject s = ScriptErrorSummary.Build(tb, Code, null);
            Assert.Equal("SyntaxError", (string)s["type"]);
            Assert.Equal(3, (int)s["line"]);
        }

        [Theory]
        [InlineData("AttributeError: 'ElementId' object has no attribute 'IntegerValue'", "hz_compat.eid_int")]
        [InlineData("AttributeError: 'ElementId' object has no attribute 'Value'", "hz_compat.eid_int")]
        [InlineData("NameError: name 'unicode' is not defined", "str(value)")]
        [InlineData("NameError: name 'basestring' is not defined", "string_types")]
        [InlineData("NameError: name 'long' is not defined", "int(value)")]
        [InlineData("AttributeError: 'Definition' object has no attribute 'ParameterType'", "parameter_data_type")]
        [InlineData("AttributeError: 'Units' object has no attribute 'DisplayUnitType'", "unit_type_id")]
        [InlineData("AttributeError: 'Definition' object has no attribute 'BuiltInParameterGroup'", "parameter_group")]
        public void Known_version_compatibility_failures_name_a_replacement(string traceback, string replacement)
        {
            JObject summary = ScriptErrorSummary.Build(traceback, "pass\n", null);
            Assert.Contains(replacement, (string)summary["hint"]);
        }

        [Fact]
        public void Nothing_to_say_is_null()
        {
            Assert.Null(ScriptErrorSummary.Build("", Code, null));
            Assert.Null(ScriptErrorSummary.Render(null));
        }
    }

    public class ModalAutoCloseTests
    {
        [Fact]
        public void External_failure_may_be_left_with_the_person_without_changing_the_save_reminder_default()
        {
            Assert.Null(ModalAutoClose.Decide("External Tools - External Tool Failure", null, new[] { "Close" }, false));
            Assert.NotNull(ModalAutoClose.Decide("Project Not Saved Recently", null, new[] { "Cancel" }, false));
        }

        [Theory]
        [InlineData(null, false, null, true)]
        [InlineData(true, false, "false", false)]
        [InlineData(true, false, "0", false)]
        [InlineData(false, false, "true", false)]
        [InlineData(null, true, null, false)]
        public void Owner_false_cannot_be_overridden_and_session_can_disable(bool? owner, bool malformed, string session, bool enabled)
        {
            var config = new JObject();
            if (owner.HasValue) config["auto_close_external_tool_failure"] = owner.Value;
            Assert.Equal(enabled, Settings.ExternalToolFailureAutoCloseFrom(config, malformed, session));
        }
        [Fact]
        public void Closes_the_save_reminder_with_cancel_and_external_tool_failure_with_close()
        {
            var save = ModalAutoClose.Decide("Project Not Saved Recently", new[] { "The project has not been saved recently." }, null);
            Assert.Equal(ModalDialogKinds.SaveReminder, save.Kind);
            Assert.Equal("Cancel (do not save)", save.Option);

            var tool = ModalAutoClose.Decide("External Tools - External Tool Failure", new[] { "Failed to initialize the add-in" }, new[] { "Close" });
            Assert.Equal(ModalDialogKinds.ExternalToolFailure, tool.Kind);
            Assert.Equal("Close", tool.Option);

            JObject j = save.ToJson("horizun_health", new System.DateTime(2026, 10, 27, 0, 0, 0, System.DateTimeKind.Utc));
            Assert.Equal("Project Not Saved Recently", (string)j["dialog"]);
            Assert.Equal("Cancel (do not save)", (string)j["option"]);
            Assert.False((bool)j["closed_verified"]);
            Assert.Null(j["model_changed"]);
        }

        [Theory]
        [InlineData("Save File")]
        [InlineData("Unresolved References")]
        [InlineData("File Opened By Another User")]
        [InlineData("Security - Unsigned Add-In")]
        [InlineData("Revit")]
        [InlineData("")]
        public void Every_other_dialog_stays_with_the_person(string title)
        {
            Assert.Null(ModalAutoClose.Decide(title, new[] { "The add-in failed" }, new[] { "OK", "Cancel" }));
        }

        [Fact]
        public void A_save_reminder_that_offers_no_way_out_but_saving_is_left_alone()
        {
            Assert.Null(ModalAutoClose.Decide("Project Not Saved Recently", null, new[] { "Save" }));
        }

        [Fact]
        public void A_body_that_mentions_an_add_in_failure_is_not_enough()
        {
            Assert.Null(ModalAutoClose.Decide("Revit", new[] { "The external application failed to load." }, null));
            Assert.Null(ModalAutoClose.Decide(new ModalDialogInfo { DialogWindowFound = false }));
        }
    }
}
