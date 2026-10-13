// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The review -> correction -> re-review cycle, this repo's half. The plans are
// shaped like horizun-review's plan_correcciones_revit output (pasos / manual /
// herramienta / argumentos / resolver) and the replies like the real typed
// replies: horizun_write_params_verified rows (target_kind, target_id as a
// string, parameter, value_read_back, confirmed, confirmed_against, outcome) and
// horizun_manage_revisions (state committed_verified, host_verified, rows keyed by
// the action key with sheet_ids).
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ReviewCorrectionPlanTests
    {
        private static bool Published(string tool) =>
            tool == "horizun_write_params_verified" || tool == "horizun_manage_revisions" ||
            tool == "horizun_execute_python";

        private static JObject Plan() => JObject.Parse(@"{
          ""documento"": ""Model A"",
          ""reglas_de_uso"": [""Llamar horizun_health...""],
          ""pasos"": [
            { ""id"": ""project-info"", ""motivo"": ""TB-001/002/003"", ""herramienta"": ""horizun_write_params_verified"",
              ""argumentos"": { ""target_document"": ""Model A"", ""dry_run"": true,
                ""writes"": [ { ""target"": ""project_info"", ""parameter"": ""PROJECT_NUMBER"", ""value"": ""P-100"" } ],
                ""transaction_name"": ""datos del proyecto"" } },
            { ""id"": ""nombres-de-hoja"", ""motivo"": ""TXT-001"", ""herramienta"": ""horizun_write_params_verified"",
              ""resolver"": { ""target_id"": ""id de cada hoja por su numero"" },
              ""argumentos"": { ""target_document"": ""Model A"", ""dry_run"": true,
                ""writes"": [ { ""target_id"": ""<id de A-101>"", ""parameter"": ""SHEET_NAME"", ""value"": ""FLOOR PLAN"" },
                              { ""target_id"": ""<id de A-102>"", ""parameter"": ""SHEET_NAME"", ""value"": ""ROOF PLAN"" } ] } },
            { ""id"": ""revision"", ""motivo"": ""REV-002"", ""herramienta"": ""horizun_manage_revisions"",
              ""resolver"": { ""sheet_ids"": ""ids de estas hojas"" },
              ""argumentos"": { ""target_document"": ""Model A"", ""dry_run"": true,
                ""actions"": [ { ""key"": ""rev-3"", ""operation"": ""create_revision"", ""description"": ""PERMIT REV 3"",
                                 ""revision_date"": ""2026-10-01"", ""sheet_ids"": [""<resolver>""] } ] } }
          ],
          ""manual"": [ { ""motivo"": ""XREF-001"", ""que"": ""a callout points nowhere"" } ]
        }");

        // ---- Check ---------------------------------------------------------------

        [Fact]
        public void Duplicate_step_ids_and_read_only_tools_are_not_executable()
        {
            JObject p = Plan();
            ((JArray)p["pasos"]).Add(p["pasos"][0].DeepClone());
            Assert.False((bool)ReviewCorrectionPlan.Check(p, Published)["runnable"]);
            p = Plan();
            p["pasos"] = new JArray(new JObject { ["id"] = "read", ["herramienta"] = "horizun_health", ["argumentos"] = new JObject { ["dry_run"] = true } });
            Assert.False((bool)ReviewCorrectionPlan.Check(p, _ => true)["runnable"]);
        }

        [Fact]
        public void Apply_corrections_accepts_a_review_plan_and_reads_the_model_after_the_apply_loop()
        {
            var root = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (root != null && !System.IO.Directory.Exists(System.IO.Path.Combine(root.FullName, "src"))) root = root.Parent;
            string command = System.IO.File.ReadAllText(System.IO.Path.Combine(root.FullName, "src", "Horizun.Revit", "Commands", "ApplyCorrectionsCommand.cs"));
            Assert.Contains("ExecuteReviewPlan(app, request)", command);
            string review = System.IO.File.ReadAllText(System.IO.Path.Combine(root.FullName, "src", "Horizun.Revit", "Commands", "ApplyCorrectionsCommand.Review.cs"));
            Assert.Contains("ReviewCorrectionPlan.ApplyAndReRead", review);
            Assert.Contains("WriteParamsCommand.ReReadReview", review);
            Assert.Contains("ManageRevisionsCommand.ReReadReview", review);
        }

        [Fact]
        public void A_plan_with_placeholders_is_requires_input_per_step_and_names_each_placeholder()
        {
            JObject r = ReviewCorrectionPlan.Check(Plan(), Published);
            var steps = ((JArray)r["steps"]).OfType<JObject>().ToList();
            Assert.Equal(ReviewCorrectionPlan.Ready, (string)steps[0]["state"]);
            Assert.Equal(ReviewCorrectionPlan.RequiresInput, (string)steps[1]["state"]);
            Assert.Contains("/argumentos/writes/0/target_id", steps[1]["placeholders"].Select(p => (string)p["pointer"]));
            Assert.Equal(2, ((JArray)steps[1]["placeholders"]).Count);
            Assert.Equal(ReviewCorrectionPlan.RequiresInput, (string)steps[2]["state"]);
            Assert.Equal(1, (int)r["manual_count"]);
            Assert.False((bool)r["runnable"]);
        }

        [Fact]
        public void Python_and_unpublished_tools_are_never_permitted_and_a_step_that_writes_is_refused()
        {
            JObject plan = Plan();
            ((JArray)plan["pasos"]).Add(JObject.Parse(@"{ ""id"": ""x"", ""motivo"": ""Z"", ""herramienta"": ""horizun_execute_python"", ""argumentos"": { ""dry_run"": true } }"));
            ((JArray)plan["pasos"]).Add(JObject.Parse(@"{ ""id"": ""y"", ""motivo"": ""Z"", ""herramienta"": ""horizun_teleport"", ""argumentos"": { ""dry_run"": true } }"));
            ((JArray)plan["pasos"]).Add(JObject.Parse(@"{ ""id"": ""z"", ""motivo"": ""Z"", ""herramienta"": ""horizun_write_params_verified"", ""argumentos"": { ""dry_run"": false, ""writes"": [] } }"));
            var steps = ((JArray)ReviewCorrectionPlan.Check(plan, Published)["steps"]).OfType<JObject>().ToList();
            Assert.Equal(ReviewCorrectionPlan.NotPermitted, (string)steps[3]["state"]);
            Assert.Equal(ReviewCorrectionPlan.NotPermitted, (string)steps[4]["state"]);
            Assert.Equal(ReviewCorrectionPlan.NotPermitted, (string)steps[5]["state"]);
            Assert.Contains("dry_run", (string)steps[5]["reason"]);
        }

        // ---- Reverify ------------------------------------------------------------

        [Fact]
        public void Fresh_reads_happen_after_all_writes_and_detect_a_later_step_overwriting_the_first()
        {
            JObject plan = Plan();
            plan["pasos"] = new JArray(plan["pasos"][0].DeepClone(), plan["pasos"][0].DeepClone());
            plan["pasos"][1]["id"] = "overwrite";
            plan["pasos"][1]["argumentos"]["writes"][0]["value"] = "P-200";
            string model = null;
            int writes = 0, reads = 0;
            JObject result = ReviewCorrectionPlan.ApplyAndReRead(plan, step =>
            {
                model = (string)step["argumentos"]["writes"][0]["value"];
                writes++;
                return ParamReply(ParamRow("project_info", "77", "PROJECT_NUMBER", model, model, true, "your_value", "confirmed"));
            }, (step, reply) =>
            {
                Assert.Equal(2, writes);
                reads++;
                string wanted = (string)step["argumentos"]["writes"][0]["value"];
                return ParamReply(ParamRow("project_info", "77", "PROJECT_NUMBER", wanted, model, wanted == model, "your_value", "not_written"));
            });
            Assert.Equal(2, reads);
            Assert.Equal("failed", (string)result["state"]);
            Assert.Equal("persistent", (string)result["steps"][0]["targets"][0]["outcome"]);
            Assert.False((bool)result["host_verified"]);
        }

        [Fact]
        public void A_failed_fresh_read_never_inherits_a_verified_apply_reply()
        {
            JObject plan = Plan();
            plan["pasos"] = new JArray(plan["pasos"][0].DeepClone());
            JObject result = ReviewCorrectionPlan.ApplyAndReRead(plan,
                _ => ParamReply(ParamRow("project_info", "77", "PROJECT_NUMBER", "P-100", "P-100", true, "your_value", "confirmed")),
                (_, __) => throw new System.InvalidOperationException("unreadable"));
            Assert.Equal("failed", (string)result["state"]);
            Assert.Equal("not_verifiable", (string)result["steps"][0]["targets"][0]["outcome"]);
        }

        private static JObject ParamRow(string kind, string id, string parameter, string requested, string readBack,
                                        bool confirmed, string against, string outcome) => new JObject
        {
            ["index"] = 0, ["target_kind"] = kind, ["target_id"] = id, ["target_name"] = "x", ["parameter"] = parameter,
            ["requested"] = requested, ["value_read_back"] = new JObject { ["readable"] = true, ["storage"] = "String", ["value"] = readBack, ["has_value"] = true, ["text"] = null }, ["confirmed"] = confirmed,
            ["confirmed_against"] = confirmed ? (JToken)against : null, ["outcome"] = outcome
        };

        private static JObject ParamReply(params JObject[] rows) => new JObject
        {
            ["mode"] = "writes", ["transaction_status"] = "Committed", ["requested"] = rows.Length,
            ["verification"] = new JObject { ["what"] = "parameter writes", ["verified"] = rows.All(r => (string)r["confirmed_against"] == "your_value") },
            ["rows_truncated"] = false, ["rows"] = new JArray(rows)
        };

        private static JObject Applied(string step, JObject arguments, JObject reply) =>
            new JObject { ["step_id"] = step, ["arguments"] = arguments, ["reply"] = reply };

        private static JObject ResolvedSheetArgs(bool dryRun = false) => JObject.Parse(@"{ ""target_document"": ""Model A"", ""dry_run"": " +
            (dryRun ? "true" : "false") + @", ""writes"": [ { ""target_id"": 4101, ""parameter"": ""SHEET_NAME"", ""value"": ""FLOOR PLAN"" },
              { ""target_id"": 4102, ""parameter"": ""SHEET_NAME"", ""value"": ""ROOF PLAN"" } ] }");

        private static JObject RevisionArgs() => JObject.Parse(@"{ ""target_document"": ""Model A"", ""dry_run"": false,
            ""actions"": [ { ""key"": ""rev-3"", ""operation"": ""create_revision"", ""description"": ""PERMIT REV 3"",
                             ""revision_date"": ""2026-10-01"", ""sheet_ids"": [4101, 4102] } ] }");

        private static JObject RevisionReply(params long[] sheets) => new JObject
        {
            ["state"] = "committed_verified", ["host_verified"] = true,
            ["rows"] = new JArray(new JObject { ["key"] = "rev-3", ["revision_id"] = 9001, ["verified"] = true, ["sheet_ids"] = new JArray(sheets) })
        };

        private static JArray AllApplied() => new JArray
        {
            Applied("project-info", (JObject)Plan()["pasos"][0]["argumentos"].DeepClone().Also(a => a["dry_run"] = false),
                ParamReply(ParamRow("project_info", "77", "PROJECT_NUMBER", "P-100", "P-100", true, "your_value", "written"))),
            Applied("nombres-de-hoja", ResolvedSheetArgs(),
                ParamReply(ParamRow("instance", "4101", "SHEET_NAME", "FLOOR PLAN", "FLOOR PLAN", true, "your_value", "written"),
                           ParamRow("instance", "4102", "SHEET_NAME", "ROOF PLAN", "ROOF PLAN", true, "your_value", "written"))),
            Applied("revision", RevisionArgs(), RevisionReply(4101, 4102))
        };

        [Fact]
        public void Relayed_replies_are_capped_and_ask_for_the_re_review()
        {
            JObject r = ReviewCorrectionPlan.Reverify(Plan(), AllApplied());
            Assert.Equal("self_reported_verified", (string)r["state"]);
            Assert.All(((JArray)r["steps"]).OfType<JObject>(), s => Assert.Equal("self_reported_verified", (string)s["state"]));
            Assert.True((bool)r["re_review"]["required"]);
            Assert.Equal(new[] { "REV-002", "TB-001/002/003", "TXT-001" }, ((JArray)r["re_review"]["rules"]).Select(t => (string)t).OrderBy(x => x));
            Assert.Equal(1, (int)r["manual_not_covered"]);
        }

        [Fact]
        public void A_row_the_model_does_not_carry_is_persistent_and_the_step_is_partial()
        {
            JArray applied = AllApplied();
            applied[1]["reply"] = ParamReply(
                ParamRow("instance", "4101", "SHEET_NAME", "FLOOR PLAN", "FLOOR PLAN", true, "your_value", "written"),
                ParamRow("instance", "4102", "SHEET_NAME", "ROOF PLAN", "ROOF PLN", false, null, "not_written"));
            JObject r = ReviewCorrectionPlan.Reverify(Plan(), applied);
            JObject step = ((JArray)r["steps"]).OfType<JObject>().Single(s => (string)s["step_id"] == "nombres-de-hoja");
            Assert.Equal("partial", (string)step["state"]);
            Assert.Equal(ReviewCorrectionPlan.Persistent, (string)step["targets"][1]["outcome"]);
            Assert.Equal("partial", (string)r["state"]);
        }

        [Fact]
        public void Applied_values_that_drift_from_the_plan_are_not_corrected_even_when_the_tool_verified_them()
        {
            JArray applied = AllApplied();
            JObject args = ResolvedSheetArgs();
            args["writes"][1]["value"] = "ROOF PLANS";
            applied[1]["arguments"] = args;
            applied[1]["reply"] = ParamReply(
                ParamRow("instance", "4101", "SHEET_NAME", "FLOOR PLAN", "FLOOR PLAN", true, "your_value", "written"),
                ParamRow("instance", "4102", "SHEET_NAME", "ROOF PLANS", "ROOF PLANS", true, "your_value", "written"));
            JObject step = ((JArray)ReviewCorrectionPlan.Reverify(Plan(), applied)["steps"]).OfType<JObject>().Single(s => (string)s["step_id"] == "nombres-de-hoja");
            Assert.Equal(ReviewCorrectionPlan.Persistent, (string)step["targets"][1]["outcome"]);
            Assert.Contains("plan", (string)step["targets"][1]["reason"]);
        }

        [Fact]
        public void A_rehearsal_reply_is_not_applied_and_a_parse_only_confirmation_is_self_reported()
        {
            JArray applied = AllApplied();
            applied[1]["arguments"] = ResolvedSheetArgs(dryRun: true);
            applied[0]["reply"] = ParamReply(ParamRow("project_info", "77", "PROJECT_NUMBER", "P-100", "P-100", true, "a_re_read_not_your_value", "written"));
            JObject r = ReviewCorrectionPlan.Reverify(Plan(), applied);
            var steps = ((JArray)r["steps"]).OfType<JObject>().ToDictionary(s => (string)s["step_id"]);
            Assert.Equal(ReviewCorrectionPlan.NotApplied, (string)steps["nombres-de-hoja"]["targets"][0]["outcome"]);
            Assert.Equal("failed", (string)steps["nombres-de-hoja"]["state"]);
            Assert.Equal(ReviewCorrectionPlan.ConfirmedByRereadOnly, (string)steps["project-info"]["targets"][0]["outcome"]);
            Assert.Equal("self_reported_verified", (string)steps["project-info"]["state"]);
            Assert.Equal("failed", (string)r["state"]);
        }

        [Fact]
        public void A_revision_missing_a_planned_sheet_is_persistent_and_a_step_with_no_reply_is_not_verifiable()
        {
            JArray applied = AllApplied();
            applied[2]["reply"] = RevisionReply(4101);
            applied.RemoveAt(0);
            JObject r = ReviewCorrectionPlan.Reverify(Plan(), applied);
            var steps = ((JArray)r["steps"]).OfType<JObject>().ToDictionary(s => (string)s["step_id"]);
            Assert.Equal(ReviewCorrectionPlan.Persistent, (string)steps["revision"]["targets"][0]["outcome"]);
            Assert.Equal(ReviewCorrectionPlan.NotVerifiable, (string)steps["project-info"]["targets"][0]["outcome"]);
            Assert.Equal("failed", (string)steps["project-info"]["state"]);
        }

        [Fact]
        public void A_truncated_reply_leaves_the_rows_it_did_not_show_not_verifiable()
        {
            JArray applied = AllApplied();
            JObject reply = ParamReply(ParamRow("instance", "4101", "SHEET_NAME", "FLOOR PLAN", "FLOOR PLAN", true, "your_value", "written"));
            reply["rows_truncated"] = true;
            applied[1]["reply"] = reply;
            JObject step = ((JArray)ReviewCorrectionPlan.Reverify(Plan(), applied)["steps"]).OfType<JObject>().Single(s => (string)s["step_id"] == "nombres-de-hoja");
            Assert.Equal(ReviewCorrectionPlan.NotVerifiable, (string)step["targets"][1]["outcome"]);
            Assert.Contains("truncated", (string)step["targets"][1]["reason"]);
        }

        [Fact]
        public void A_reply_wrapped_as_the_mcp_structured_content_is_read_the_same()
        {
            JArray applied = AllApplied();
            foreach (JObject a in applied.OfType<JObject>())
                a["reply"] = new JObject { ["structuredContent"] = new JObject { ["ok"] = true, ["result"] = a["reply"].DeepClone() } };
            Assert.Equal("self_reported_verified", (string)ReviewCorrectionPlan.Reverify(Plan(), applied)["state"]);
        }
    }

    internal static class JTokenTestExtensions
    {
        public static JObject Also(this JToken t, System.Action<JObject> change)
        {
            var o = (JObject)t;
            change(o);
            return o;
        }
    }
}
