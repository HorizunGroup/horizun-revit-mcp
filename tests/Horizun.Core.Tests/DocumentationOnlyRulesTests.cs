using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    // horizun_execute_python documentation_only=true: the diff of the run is judged as
    // permission_profile=documentation judges targets, and uncertainty refuses.
    public class DocumentationOnlyRulesTests
    {
        private static DocumentationTarget T(string kind, string cat = null) => new DocumentationTarget { Kind = kind, Category = cat };

        private static RehearsalChanges Diff(long[] created = null, long[] modified = null, long[] deleted = null)
        {
            var c = new RehearsalChanges();
            c.Record(created, deleted, modified);
            return c;
        }

        [Fact]
        public void Moving_a_wall_is_refused_with_its_id()
        {
            var v = DocumentationOnlyRules.Judge(Diff(modified: new long[] { 501 }),
                id => T(DocumentationTarget.ModelKind, "Walls"), id => null, false, null);
            Assert.False(v.Ok);
            var o = Assert.Single(v.Offenders);
            Assert.Equal(501, o.Id);
            Assert.Equal("modified", o.Change);
            Assert.Contains("501", DocumentationOnlyRules.FailureMessage(v, true));
        }

        [Fact]
        public void Views_sheets_and_annotation_pass()
        {
            var v = DocumentationOnlyRules.Judge(
                Diff(created: new long[] { 1, 2 }, modified: new long[] { 3 }),
                id => id == 1 ? T(DocumentationTarget.ViewKind) : id == 2 ? T(DocumentationTarget.AnnotationKind) : T(DocumentationTarget.ViewOwnedKind),
                id => null, false, null);
            Assert.True(v.Ok);
            Assert.Empty(v.Offenders);
            Assert.Equal(2, v.ChecksCreated);
        }

        [Theory]
        [InlineData("model")]
        [InlineData("missing")]
        public void A_type_family_material_or_model_element_is_an_offender(string kind)
        {
            var v = DocumentationOnlyRules.Judge(Diff(created: new long[] { 9 }), id => T(kind), id => null, false, null);
            Assert.False(v.Ok);
            Assert.Equal(new long[] { 9 }, v.Offenders.Select(o => o.Id).ToArray());
        }

        [Fact]
        public void An_unclassifiable_id_refuses_instead_of_passing()
        {
            var v = DocumentationOnlyRules.Judge(Diff(modified: new long[] { 7 }), id => null, id => null, false, null);
            Assert.False(v.Ok);
            Assert.Equal(DocumentationOnlyRules.Unclassified, v.Offenders.Single().Kind);
        }

        [Fact]
        public void A_deletion_is_judged_from_the_snapshot_and_outside_it_refuses()
        {
            var inside = DocumentationOnlyRules.Judge(Diff(deleted: new long[] { 40 }), id => null,
                id => DocumentationTarget.AnnotationKind, false, null);
            Assert.True(inside.Ok);
            var outside = DocumentationOnlyRules.Judge(Diff(deleted: new long[] { 41 }), id => null, id => null, false, null);
            Assert.False(outside.Ok);
            Assert.Equal("deleted", outside.Offenders.Single().Change);
        }

        [Fact]
        public void A_change_in_another_document_or_an_unreadable_stream_refuses_with_no_offender_id()
        {
            var other = DocumentationOnlyRules.Judge(Diff(), id => null, id => null, true, null);
            Assert.False(other.Ok);
            Assert.Empty(other.Offenders);
            Assert.Single(other.Uncertainties);
            var broken = DocumentationOnlyRules.Judge(Diff(), id => null, id => null, false, "boom");
            Assert.False(broken.Ok);
        }

        [Fact]
        public void An_empty_diff_passes()
        {
            Assert.True(DocumentationOnlyRules.Judge(Diff(), id => null, id => null, false, null).Ok);
        }

        [Fact]
        public void The_report_names_the_offending_ids_and_the_rollback_proof()
        {
            var v = DocumentationOnlyRules.Judge(Diff(modified: new long[] { 501, 502 }),
                id => T(DocumentationTarget.ModelKind, "Walls"), id => null, false, null);
            var r = DocumentationOnlyRules.Report(v, "rolled_back", true, true, 12);
            Assert.False((bool)r["ok"]);
            Assert.Equal("rolled_back", (string)r["outcome"]);
            Assert.Equal(new long[] { 501, 502 }, r["offending_ids"].Select(t => (long)t).ToArray());
            Assert.True((bool)r["rollback_verified"]);
            Assert.Equal(2, (int)r["offenders_total"]);
        }

        private static string Source(string relative, [CallerFilePath] string here = "") =>
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(here), "..", "..", relative.Replace('/', Path.DirectorySeparatorChar)));

        [Fact]
        public void Incomplete_observation_and_script_failure_cannot_keep_a_documentation_group()
        {
            string tracker = Source("src/Horizun.Revit/Commands/DocumentationOnlyTracker.cs");
            string command = Source("src/Horizun.Revit/Commands/ExecutePythonCommand.cs");
            Assert.Contains("snapshotComplete ? listenError", tracker);
            Assert.Contains("if (listenError != null || otherDocument || !snapshotComplete) return false", tracker);
            Assert.Contains("leftModifiable, interrupted, error != null, out string docOnlyFailure", command);
            Assert.Contains("if (scriptFailed && !interrupted)", command);
        }

        [Fact]
        public void A_documentation_only_run_opens_exactly_one_transaction_group()
        {
            // Review of 6.0.0: an 'else if (!readOnly)' detached from the documentation branch
            // opened a second, nested "python" group over the documentation one and left it open.
            string py = Source("src/Horizun.Revit/Commands/ExecutePythonCommand.cs");
            int normal = py.IndexOf("docGroup = new TransactionGroup(doc, \"Horizun: python\")");
            Assert.True(normal > 0);
            string guard = py.Substring(py.LastIndexOf("if (", normal), normal - py.LastIndexOf("if (", normal));
            Assert.Contains("!documentationOnly", guard);
            Assert.Contains("!readOnly", guard);
            Assert.DoesNotContain("else if (!readOnly)", py);
        }

        [Fact]
        public void Documentation_group_must_start_before_observer_and_script()
        {
            string py = Source("src/Horizun.Revit/Commands/ExecutePythonCommand.cs");
            int start = py.IndexOf("if (docGroup.Start() != TransactionStatus.Started)", py.IndexOf("if (documentationOnly)\n"));
            int observer = py.IndexOf("DocumentationOnlyTracker.Start(app.Application, doc)", start);
            Assert.True(start >= 0 && observer > start);
        }

        [Fact]
        public void The_contract_declares_documentation_only_on_execute_python()
        {
            var props = (Newtonsoft.Json.Linq.JObject)Horizun.Contracts.Contract.Find("horizun_execute_python").InputSchema["properties"];
            Assert.Equal("boolean", (string)props["documentation_only"]["type"]);
            Assert.False((bool)props["documentation_only"]["default"]);
            Assert.Contains("documentacion_violada", (string)props["documentation_only"]["description"]);
        }

        [Fact]
        public void The_python_command_judges_the_diff_inside_a_group_and_declares_the_offenders()
        {
            string py = Source("src/Horizun.Revit/Commands/ExecutePythonCommand.cs");
            Assert.Contains("request.Value<bool?>(\"documentation_only\")", py);
            Assert.Contains("DocumentationOnlyTracker.Start(app.Application, doc)", py);
            Assert.Contains("group.Assimilate()", py);
            Assert.Contains("Guard.RollBack(group).Confirmed", py);
            Assert.Contains("raised[ErrorCodes.CodeField] = ErrorCodes.DocumentacionViolada", py);
            Assert.Contains("raised[\"offending_ids\"]", py);
            // The textual Save/Close/Open scan guards this mode exactly as it guards read_only.
            Assert.Contains("(readOnly || documentationOnly) && readOnlyViolations.Count > 0", py);
            // And the group is judged BEFORE it is closed: judge, then assimilate or roll back.
            Assert.True(py.IndexOf("tracker.Judge()") < py.IndexOf("group.Assimilate()"));
        }

        [Fact]
        public void The_refusal_code_is_stable_and_in_the_catalog()
        {
            Assert.True(ErrorCodes.IsStable(DocumentationOnlyRules.ErrorCode));
            Assert.Contains(DocumentationOnlyRules.ErrorCode, ErrorCodes.Catalog);
        }
    }
}
