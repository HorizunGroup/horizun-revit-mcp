using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;
using VerifyStage = Horizun.Revit.Core.DimensionPlanRules.VerifyStage;

namespace Horizun.Core.Tests
{
    public sealed class TextNoteVerificationTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Rehearsal_does_not_regenerate_after_the_last_creation_spends_the_budget_or_loses_the_caller(
            bool bestEffort, bool cancelled)
        {
            var (doc, view, app, request) = BudgetFixture(bestEffort);
            bool created = false;
            doc.TestAfterAnnotationCreation = () => created = true;
            var budget = new AnnotationBudget(() => created && !cancelled ? 10 : 0, 10, () => created && cancelled);

            CommandResult result = new AnnotateCommand().Execute(app, request.ToString(), budget);

            Assert.Equal(0, doc.Regenerations);
            AssertRehearsalStopped(doc, view, result, cancelled);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Rehearsal_does_not_issue_a_token_if_its_budget_or_caller_is_lost_during_rollback(
            bool bestEffort, bool cancelled)
        {
            var (doc, view, app, request) = BudgetFixture(bestEffort);
            bool rolledBack = false;
            doc.CaptureFixtureState = () => () => rolledBack = true;
            var budget = new AnnotationBudget(() => rolledBack && !cancelled ? 10 : 0, 10, () => rolledBack && cancelled);

            CommandResult result = new AnnotateCommand().Execute(app, request.ToString(), budget);

            Assert.True(rolledBack);
            Assert.Equal(1, doc.Regenerations);
            AssertRehearsalStopped(doc, view, result, cancelled);
        }

        private static void AssertRehearsalStopped(Document doc, View view, CommandResult result, bool cancelled)
        {
            Assert.False(result.Success);
            Assert.Equal(cancelled ? ErrorCodes.Cancelado : ErrorCodes.AnnotationBudgetExhausted,
                ErrorCodes.Classify(result.Error, result.Detail).Code);
            Assert.Equal(10, result.Detail.Value<int>("budget_ms"));
            Assert.Equal(cancelled ? 0 : 10, result.Detail.Value<int>("elapsed_ms"));
            Assert.Equal(1, result.Detail.Value<int>("rows_attempted"));
            Assert.Equal("rolled_back", result.Detail.Value<string>("state"));
            Assert.Equal("RolledBack", result.Detail.Value<string>("rehearsal_rollback_status"));
            Assert.True(result.Detail.Value<bool>("rollback_confirmed"));
            Assert.Null(result.Detail["confirmation_token"]);
            Assert.Same(view, Assert.Single(doc.Elements).Value);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Exhausting_annotation_budget_rolls_back_preview_or_unverified_apply(bool bestEffort, bool apply)
        {
            var (doc, view, app, request) = BudgetFixture(bestEffort);
            var command = new AnnotateCommand();
            if (apply)
            {
                CommandResult preview = command.Execute(app, request.ToString());
                Assert.True(preview.Success, preview.Error);
                request["dry_run"] = false;
                request["confirmation_token"] = ((JObject)preview.Data).Value<string>("confirmation_token");
                request["idempotency_key"] = "apply-with-budget";
            }

            long elapsed = 0;
            int commits = 0;
            doc.TestAfterCommit = () => { commits++; if (apply) elapsed = 10; };
            doc.TestRegenerate = () => { if (!apply) elapsed = 10; };
            CommandResult result = command.Execute(app, request.ToString(), new AnnotationBudget(() => elapsed, 10));

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.AnnotationBudgetExhausted, ErrorCodes.Classify(result.Error, result.Detail).Code);
            Assert.Equal(10, result.Detail.Value<int>("budget_ms"));
            Assert.Equal(10, result.Detail.Value<int>("elapsed_ms"));
            Assert.Equal(1, result.Detail.Value<int>("rows_attempted"));
            Assert.Equal("rolled_back", result.Detail.Value<string>("state"));
            Assert.True(result.Detail.Value<bool>("rollback_confirmed"));
            Assert.Null(result.Detail["confirmation_token"]);
            Assert.Equal(apply ? 1 : 0, commits);
            Assert.False(doc.Assimilated);
            Assert.Same(view, Assert.Single(doc.Elements).Value);
        }

        private static (Document Doc, View View, UIApplication App, JObject Request) BudgetFixture(bool bestEffort)
        {
            var doc = new Document { TestConfirmationStore = new ConfirmationStore() };
            var view = new View3D { Id = new ElementId(7) };
            doc.Elements.Add(7, view);
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc, ActiveGraphicalView = view } };
            doc.ReadLeaderTarget = (_, _, _) => LinkedFace("door-a", "inner-a");
            var request = new JObject
            {
                ["actions"] = new JArray(new JObject { ["operation"] = "text", ["view_id"] = 7,
                    ["text"] = "Door note", ["leader_target"] = new JObject { ["link_instance_id"] = 700,
                        ["linked_element_id"] = 42, ["point"] = new JArray(5, 6, 7) } })
            };
            if (bestEffort) request["on_failure"] = "best_effort";
            return (doc, view, app, request);
        }

        [Theory]
        [InlineData(false, 42, "complete", true)]
        [InlineData(false, 43, "complete", false)]
        [InlineData(false, 42, "partial", false)]
        [InlineData(true, 42, "complete", true)]
        [InlineData(true, 43, "complete", false)]
        [InlineData(true, 42, "partial", false)]
        public void Apply_rereads_the_committed_leader_and_rolls_back_if_its_target_cannot_be_confirmed(
            bool bestEffort, long actualId, string coverage, bool expected)
        {
            var document = new Document();
            var view = new View();
            var rayView = new View3D();
            var originalEnd = new XYZ(5, 6, 7);
            var storedEnd = new XYZ(5, 6.0002, 7);
            int commits = 0;
            var measurements = new List<(View View, View3D RayView, XYZ End, int Commits, bool Assimilated)>();
            document.TestAfterCommit = () =>
            {
                if (++commits == 1) document.Elements.Values.OfType<TextNote>().Single().StoredEnd = storedEnd;
            };
            document.ReadLeaderTarget = (readView, readRayView, end) =>
            {
                measurements.Add((readView, readRayView, end, commits, document.Assimilated));
                return commits == 0 ? HostFace(42)
                    : HostFace(actualId, coverage);
            };

            CommandResult result = new AnnotateCommand().ApplyTextNotes(document, view, rayView, bestEffort, false, originalEnd);

            Assert.Equal(2, commits);
            Assert.Equal(new[] { (view, rayView, originalEnd, 0, false), (view, rayView, storedEnd, 2, false) }, measurements);
            Assert.Equal(expected, result.Success);
            Assert.Equal(expected, document.Assimilated);
            Assert.Equal(expected ? 0 : 1, document.TestRollbacks);
            Assert.Equal(expected ? 1 : 0, document.Elements.Count);
            JObject payload = expected ? (JObject)result.Data : result.Detail;
            Assert.Equal("Committed", payload.Value<string>("transaction_status"));
            Assert.Equal(expected ? "Committed" : "RolledBack", payload.Value<string>("transaction_group_status"));
            JToken row = Assert.Single(payload["rows"]);
            Assert.Equal(expected, row.Value<bool>("verified"));
            Assert.Equal(bestEffort ? (expected ? "created" : "failed") : null, row.Value<string>("outcome"));
            Assert.Equal(actualId, row["text_layout"]["leader"]["hit"].Value<long>("element_id"));
            Assert.Equal(coverage, row["text_layout"]["leader"]["hit"].Value<string>("coverage"));
            if (!expected)
            {
                Assert.True(payload.Value<bool>("rollback_confirmed"));
                Assert.Contains("re-read end", row.Value<string>("reason"));
            }
        }

        [Theory]
        [InlineData(false, TransactionStatus.RolledBack)]
        [InlineData(true, TransactionStatus.RolledBack)]
        [InlineData(false, TransactionStatus.Pending)]
        [InlineData(true, TransactionStatus.Error)]
        public void Apply_reports_only_confirmed_rollbacks_after_a_mixed_batch_fails_verification(
            bool bestEffort, TransactionStatus rollbackStatus)
        {
            var document = new Document { TestGroupRollbackStatus = rollbackStatus };
            bool confirmed = rollbackStatus == TransactionStatus.RolledBack;
            var firstEnd = new XYZ(5, 6, 7);
            var secondEnd = new XYZ(8, 6, 7);
            int commits = 0;
            document.TestAfterCommit = () => commits++;
            document.ReadLeaderTarget = (_, _, end) => HostFace(commits > 0 && end == secondEnd ? 43 : 42);

            CommandResult result = new AnnotateCommand().ApplyTextNotes(document, new View(), new View3D(), bestEffort, false, firstEnd, secondEnd);

            Assert.False(result.Success);
            Assert.Equal(confirmed, result.Detail.Value<bool>("rollback_confirmed"));
            Assert.Equal(rollbackStatus.ToString(), result.Detail.Value<string>("transaction_group_status"));
            Assert.Equal(confirmed ? "rolled_back" : "uncertain", result.Detail.Value<string>("state"));
            Assert.Equal(2, commits);
            Assert.Equal(1, document.TestRollbacks);
            Assert.False(document.Assimilated);
            Assert.Equal(confirmed ? 0 : 2, document.Elements.Count);
            Assert.Contains(rollbackStatus.ToString(), result.Error);
            if (confirmed)
            {
                Assert.Contains("was rolled back", result.Error);
                Assert.Contains("Nothing from this call remains", result.Error);
            }
            else
            {
                Assert.DoesNotContain("was rolled back", result.Error);
                Assert.DoesNotContain("Nothing from this call remains", result.Error);
                Assert.Contains("re-read", result.Error);
            }
            bool? inModel = confirmed ? false : (bool?)null;
            Assert.Collection(result.Detail["rows"],
                row =>
                {
                    Assert.Equal(0, row.Value<int>("index"));
                    Assert.False(row.Value<bool>("verified"));
                    Assert.Equal(inModel, row.Value<bool?>("in_model"));
                    Assert.Equal(bestEffort ? (confirmed ? "rolled_back" : "uncertain") : null, row.Value<string>("outcome"));
                    Assert.Equal(42, row["text_layout"]["leader"]["hit"].Value<long>("element_id"));
                },
                row =>
                {
                    Assert.Equal(1, row.Value<int>("index"));
                    Assert.False(row.Value<bool>("verified"));
                    Assert.Equal(inModel, row.Value<bool?>("in_model"));
                    Assert.Equal(bestEffort ? "failed" : null, row.Value<string>("outcome"));
                    Assert.Contains("re-read end", row.Value<string>("reason"));
                    Assert.Equal(43, row["text_layout"]["leader"]["hit"].Value<long>("element_id"));
                });
        }

        [Theory]
        [InlineData(false, "complete", false)]
        [InlineData(true, "complete", false)]
        [InlineData(false, "complete", true)]
        [InlineData(true, "complete", true)]
        [InlineData(false, "partial", false)]
        [InlineData(true, "partial", false)]
        public void Apply_of_a_point_only_leader_checks_the_ray_from_the_stored_end(
            bool bestEffort, string coverage, bool hitsElement)
        {
            var document = new Document();
            var plannedEnd = new XYZ(2, 3, 4);
            var storedEnd = new XYZ(2, 3.0002, 4);
            int commits = 0;
            var ends = new List<XYZ>();
            document.TestAfterCommit = () =>
            {
                if (++commits == 1) document.Elements.Values.OfType<TextNote>().Single().StoredEnd = storedEnd;
            };
            document.ReadLeaderTarget = (_, _, end) =>
            {
                ends.Add(end);
                if (commits == 0 || hitsElement) return HostFace(42);
                if (coverage == "partial") return HostFace(43);
                return new LeaderHit { Cast = true, Coverage = "complete", Uncertainty = "the ray met nothing" };
            };

            document.TestUnmeasuredCutLink = () => commits > 0 && coverage == "partial" ? "linked geometry at the cut plane was not measured" : null;
            CommandResult result = new AnnotateCommand().ApplyTextNotes(document, new ViewPlan(), new View3D(), bestEffort, true, plannedEnd);

            bool expected = hitsElement || coverage == "partial";
            Assert.Equal(expected, result.Success);
            Assert.Equal(expected, document.Assimilated);
            Assert.Equal(new[] { plannedEnd, storedEnd }, ends);
            JObject payload = expected ? (JObject)result.Data : result.Detail;
            JToken row = Assert.Single(payload["rows"]);
            Assert.Equal(expected, row.Value<bool>("verified"));
            var hit = row["text_layout"]["leader"]["hit"];
            Assert.True(row["text_layout"]["leader"].Value<bool>("end_match"));
            Assert.Equal(coverage, hit.Value<string>("coverage"));
            Assert.Equal(hitsElement, hit.Value<bool?>("hit"));
            if (coverage == "partial")
            {
                Assert.Equal(false, hit.Value<bool?>("proven"));
                Assert.Null(hit["element_id"]);
                Assert.Contains("linked geometry", hit.Value<string>("uncertainty"));
                JObject firstRayHit = Assert.IsType<JObject>(hit["first_ray_hit"]);
                Assert.Equal("host", firstRayHit.Value<string>("source"));
                Assert.Equal(43, firstRayHit.Value<long>("element_id"));
            }
            if (!expected)
            {
                Assert.Contains("the arrow would point at nothing", row.Value<string>("reason"));
                Assert.True(payload.Value<bool>("rollback_confirmed"));
                Assert.Empty(document.Elements);
            }
        }

        [Theory]
        [InlineData(false, "door-a", "inner-a", true)]
        [InlineData(true, "door-a", "inner-a", true)]
        [InlineData(false, "door-b", "inner-a", false)]
        [InlineData(true, "door-b", "inner-b", false)]
        [InlineData(false, "door-a", "inner-b", false)]
        [InlineData(true, null, "inner-a", false)]
        public void Apply_checks_the_planned_nested_target_identity_after_commit(
            bool bestEffort, string uniqueId, string innerLink, bool expected)
        {
            var document = new Document();
            var view = new View3D();
            var plannedEnd = new XYZ(5, 6, 7);
            var storedEnd = new XYZ(5, 6.0002, 7);
            var plannedHit = LinkedFace("door-a", "inner-a");
            document.TestAfterCommit = () => document.Elements.Values.OfType<TextNote>().Single().StoredEnd = storedEnd;
            document.ReadLeaderTarget = (_, _, end) => end == plannedEnd ? plannedHit : LinkedFace(uniqueId, innerLink);

            var result = new AnnotateCommand().ApplyTextNotes(document, view, view, bestEffort, false, plannedHit, plannedEnd);

            Assert.Equal(expected, result.Success);
            Assert.Equal(expected, document.Assimilated);
            JObject payload = expected ? (JObject)result.Data : result.Detail;
            JToken row = Assert.Single(payload["rows"]);
            var hit = row["text_layout"]["leader"]["hit"];
            Assert.Equal(42, hit.Value<long>("element_id"));
            Assert.Equal(700, hit.Value<long>("link_instance_id"));
            Assert.Equal(uniqueId, hit.Value<string>("unique_id"));
            Assert.Equal(new[] { "outer", innerLink }, hit["link_path_unique_ids"].Values<string>());
            if (!expected)
            {
                Assert.Contains("planned target identity", row.Value<string>("reason"));
                Assert.True(payload.Value<bool>("rollback_confirmed"));
                Assert.Empty(document.Elements);
            }
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        [InlineData(true, false)]
        public void Apply_preserves_rows_and_observed_status_when_group_rollback_throws(bool bestEffort, bool pendingCommit)
        {
            var document = new Document
            {
                TestGroupRollbackError = new InvalidOperationException("the document is in failure handling"),
                TestGroupDisposeError = new InvalidOperationException("the group is still open"),
                TestPendingDisposeError = new InvalidOperationException("the transaction is still pending"),
                TestPendingCommit = pendingCommit
            };
            var end = new XYZ(5, 6, 7);
            var other = new XYZ(5, 6.0002, 7);
            document.TestAfterCommit = () => document.Elements.Values.OfType<TextNote>().Single().StoredEnd = other;
            document.ReadLeaderTarget = (_, _, point) => HostFace(point == end ? 42 : 43);

            var result = new AnnotateCommand().ApplyTextNotes(document, new View(), new View3D(), bestEffort, false, end);

            Assert.False(result.Success);
            Assert.Equal("uncertain", result.Detail.Value<string>("state"));
            Assert.Equal(pendingCommit ? "Pending" : "Committed", result.Detail.Value<string>("transaction_status"));
            Assert.Contains("Started (RollBack threw: InvalidOperationException", result.Detail.Value<string>("transaction_group_status"));
            Assert.Contains("the document is in failure handling", result.Error);
            Assert.Contains("Re-read the model", result.Error);
            Assert.DoesNotContain("Nothing was written", result.Error);
            Assert.DoesNotContain("Retry in smaller batches", result.Error);
            Assert.Contains("the group is still open", result.Detail.Value<string>("transaction_group_dispose_error"));
            if (pendingCommit)
                Assert.Contains("the transaction is still pending", result.Detail.Value<string>("transaction_dispose_error"));
            Assert.False(result.Detail.Value<bool>("rollback_confirmed"));
            var row = Assert.Single(result.Detail["rows"]);
            Assert.False(row.Value<bool>("verified"));
            Assert.Equal(JTokenType.Null, row["in_model"].Type);
            Assert.Equal(pendingCommit ? 42 : 43, row["text_layout"]["leader"]["hit"].Value<long>("element_id"));
            Assert.Equal(1, document.TestRollbacks);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Pending_verification_transaction_and_dispose_exception_preserve_precommit_evidence(bool bestEffort)
        {
            var document = new Document
            {
                TestPendingCommitFor = name => name == "annotate (materialise for verification)",
                TestPendingDisposeError = new InvalidOperationException("verification transaction is pending"),
                TestGroupRollbackError = new InvalidOperationException("the document is in failure handling")
            };
            int reads = 0;
            document.ReadLeaderTarget = (_, _, _) => { reads++; return HostFace(42); };

            var result = new AnnotateCommand().ApplyTextNotes(document, new View(), new View3D(), bestEffort, false, new XYZ(5, 6, 7));

            Assert.False(result.Success);
            Assert.Equal(2, document.TestTransactionStarts);
            Assert.Equal(1, reads);
            Assert.False(document.Assimilated);
            Assert.Equal("Committed", result.Detail.Value<string>("transaction_status"));
            Assert.Equal("Pending", result.Detail.Value<string>("verification_transaction_status"));
            Assert.Contains("verification transaction is pending", result.Detail.Value<string>("verification_transaction_dispose_error"));
            Assert.Contains("the document is in failure handling", result.Detail.Value<string>("transaction_group_status"));
            Assert.Equal("uncertain", result.Detail.Value<string>("state"));
            Assert.False(result.Detail.Value<bool>("rollback_confirmed"));
            var row = Assert.Single(result.Detail["rows"]);
            Assert.False(row.Value<bool>("verified"));
            Assert.Equal(JTokenType.Null, row["in_model"].Type);
            Assert.Equal(42, row["text_layout"]["leader"]["hit"].Value<long>("element_id"));
        }

        [Fact]
        public void Best_effort_returns_the_latest_verification_when_a_survivor_fails_after_removal()
        {
            var document = new Document();
            var survivorEnd = new XYZ(5, 6, 7);
            var secondSurvivorEnd = new XYZ(6, 6, 7);
            var lastSurvivorEnd = new XYZ(7, 6, 7);
            var droppedEnd = new XYZ(8, 6, 7);
            bool afterRemoval = false;
            document.ReadLeaderTarget = (_, _, end) => HostFace(end == droppedEnd ? 43 :
                !afterRemoval || end == lastSurvivorEnd ? 42 : end == survivorEnd ? 44 : 45);
            document.TestAfterDelete = () => afterRemoval = true;

            var result = new AnnotateCommand().ApplyTextNotes(document, new View(), new View3D(), true, false,
                survivorEnd, secondSurvivorEnd, lastSurvivorEnd, droppedEnd);

            Assert.False(result.Success);
            Assert.True(result.Detail.Value<bool>("rollback_confirmed"));
            Assert.Contains("row 0 no longer verifies", result.Error);
            Assert.Contains("all failed rows: 0, 1", result.Error);
            Assert.Empty(document.Elements);
            Assert.Collection(result.Detail["rows"],
                survivor =>
                {
                    Assert.Equal(0, survivor.Value<int>("index"));
                    Assert.Contains("re-read end", survivor.Value<string>("reason"));
                    Assert.Equal(44, survivor["text_layout"]["leader"]["hit"].Value<long>("element_id"));
                },
                secondSurvivor =>
                {
                    Assert.Equal(1, secondSurvivor.Value<int>("index"));
                    Assert.Contains("re-read end", secondSurvivor.Value<string>("reason"));
                    Assert.Equal(45, secondSurvivor["text_layout"]["leader"]["hit"].Value<long>("element_id"));
                },
                lastSurvivor =>
                {
                    Assert.Equal(2, lastSurvivor.Value<int>("index"));
                    Assert.Equal(42, lastSurvivor["text_layout"]["leader"]["hit"].Value<long>("element_id"));
                    Assert.False(lastSurvivor.Value<bool>("in_model"));
                },
                dropped =>
                {
                    Assert.Equal(3, dropped.Value<int>("index"));
                    Assert.Equal("verify_before_commit", dropped.Value<string>("stage"));
                    Assert.Equal(43, dropped["verification"]["text_layout"]["leader"]["hit"].Value<long>("element_id"));
                });
        }

        [Theory]
        [InlineData(false, "inner-a", true)]
        [InlineData(false, "inner-b", false)]
        [InlineData(true, "inner-a", true)]
        [InlineData(true, "inner-b", false)]
        public void Changing_the_nested_link_path_invalidates_the_rehearsal_token(bool bestEffort, string currentInnerLink, bool accepted)
        {
            var doc = new Document { TestConfirmationStore = new ConfirmationStore() };
            var view = new View3D { Id = new ElementId(7) };
            doc.Elements.Add(7, view);
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc, ActiveGraphicalView = view } };
            var end = new XYZ(5, 6, 7);
            var hit = LinkedFace("door-a", "inner-a");
            doc.ReadLeaderTarget = (_, _, _) => hit;
            var command = new AnnotateCommand();
            var request = new JObject
            {
                ["actions"] = new JArray(new JObject { ["operation"] = "text", ["view_id"] = 7,
                    ["text"] = "Door note", ["leader_target"] = new JObject { ["link_instance_id"] = 700,
                        ["linked_element_id"] = 42, ["point"] = new JArray(end.X, end.Y, end.Z) } })
            };
            if (bestEffort) request["on_failure"] = "best_effort";

            var preview = command.Execute(app, request.ToString());

            Assert.True(preview.Success, preview.Error);
            var previewData = (JObject)preview.Data;
            Assert.True(previewData["rehearsal"].Value<bool>("all_constructible"));
            Assert.True(previewData["rehearsal"].Value<bool>("rolled_back_confirmed"));
            Assert.Empty(doc.Elements.Values.OfType<TextNote>());
            Assert.Equal(1, doc.TestTransactionStarts);
            string token = previewData.Value<string>("confirmation_token");
            Assert.False(string.IsNullOrEmpty(token));
            request["dry_run"] = false;
            request["confirmation_token"] = token;
            request["idempotency_key"] = "apply-note";
            hit = LinkedFace("door-a", currentInnerLink);

            var result = command.Execute(app, request.ToString());

            Assert.Equal(accepted, result.Success);
            Assert.Equal(accepted, doc.Assimilated);
            Assert.Equal(accepted ? 4 : 2, doc.TestTransactionStarts);
            if (accepted) Assert.Equal(end.X, Assert.Single(doc.Elements.Values.OfType<TextNote>()).StoredEnd.X);
            else
            {
                Assert.Equal("StalePlan", result.Detail.Value<string>("confirmation_state"));
                Assert.Empty(doc.Elements.Values.OfType<TextNote>());
            }
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        [InlineData(true, false)]
        public void Fatal_verification_regeneration_aborts_the_group_without_a_model_read(bool bestEffort, bool fatal)
        {
            var document = new Document();
            int reads = 0, regenerations = 0;
            document.ReadLeaderTarget = (_, _, _) => { reads++; return HostFace(42); };
            document.TestRegenerate = () =>
            {
                if (++regenerations != 2) return;
                if (fatal) throw new Autodesk.Revit.Exceptions.RegenerationFailedException();
                throw new InvalidOperationException("auxiliary regeneration unavailable");
            };

            var result = new AnnotateCommand().ApplyTextNotes(document, new View(), new View3D(), bestEffort, false, new XYZ(5, 6, 7));

            Assert.Equal(!fatal, result.Success);
            Assert.Equal(!fatal, document.Assimilated);
            Assert.Equal(fatal ? 1 : 2, reads);
            if (!fatal) return;
            Assert.Equal("Committed", result.Detail.Value<string>("transaction_status"));
            Assert.Equal("RolledBack", result.Detail.Value<string>("verification_transaction_status"));
            Assert.Equal("RolledBack", result.Detail.Value<string>("transaction_group_status"));
            Assert.True(result.Detail.Value<bool>("rollback_confirmed"));
            Assert.Empty(document.Elements);
            var row = Assert.Single(result.Detail["rows"]);
            Assert.Equal(42, row["text_layout"]["leader"]["hit"].Value<long>("element_id"));
        }

        internal static LeaderHit HostFace(long id, string coverage = "complete")
            => new LeaderHit { Cast = true, Coverage = coverage, Source = "host", ElementId = id, UniqueId = "host-" + id };

        private static LeaderHit LinkedFace(string uid, string innerLink) => new LeaderHit
        {
            Cast = true, Coverage = "complete", Source = "link", ElementId = 42, UniqueId = uid,
            LinkInstanceId = 700, LinkInstanceUniqueId = "outer", LinkDepth = 2, LinkPath = new[] { "outer", innerLink }
        };
    }
}

// Core compiles the production apply paths and note verifier. These doubles supply
// a valid rehearsed plan, element creation, the stored note layout and the Revit ray.
namespace Autodesk.Revit.DB
{
    public class Leader { public XYZ End { get; set; } }
    public class Dimension : Element { }
    public class SpatialElementTag : Element { public string TagText => throw new NotSupportedException(); }
    public enum HorizontalTextAlignment { Left }
    public enum VerticalTextAlignment { Top }
    public partial class TextNote
    {
        public XYZ StoredEnd { get; set; }
        public string Text { get; set; }
        public TextNoteType TextNoteType { get; set; }
        public override ElementId GetTypeId() => TextNoteType.Id;
        public HorizontalTextAlignment HorizontalAlignment => throw new NotSupportedException();
        public VerticalTextAlignment VerticalAlignment => throw new NotSupportedException();
        public XYZ BaseDirection => throw new NotSupportedException();
    }
    public partial class View
    {
        public XYZ UpDirection => throw new NotSupportedException();
        public XYZ RightDirection => throw new NotSupportedException();
    }
    public partial class Document
    {
        internal Func<View, View3D, XYZ, LeaderHit> ReadLeaderTarget;
        internal Func<ViewPlan, XYZ, IReadOnlyList<PlanProbeCandidate>> TestPlanCandidates;
        internal long? TestPlanRegionId;
        internal Func<string> TestUnmeasuredCutLink;
        internal View3D TestFallbackRayView;
        internal Exception TestGroupRollbackError, TestGroupDisposeError, TestPendingDisposeError;
        internal Func<string, bool> TestPendingCommitFor;
        internal Action TestAfterDelete, TestRegenerate, TestAfterAnnotationCreation;
        internal bool TestPendingCommit;
        internal TransactionStatus TestGroupRollbackStatus = TransactionStatus.RolledBack;
    }
}

namespace Autodesk.Revit.Exceptions
{
    public class RegenerationFailedException : ApplicationException { }
}

namespace Horizun.Revit.Commands
{
    internal static partial class LeaderTargetReader
    {
        public static View3D FindView3D(Document doc) => doc.TestFallbackRayView;
        public static LeaderHit Cast(Document doc, View view, View3D rayView, XYZ end) => CastRay(doc, view, rayView, end);
        private static bool LinkIsDisplayed(View view, RevitLinkInstance link) =>
            link.GetLinkDocument() != null && !link.TestHiddenInOwnerView;
        private static LeaderHit CastRay(Document doc, View view, View3D rayView, XYZ end) => doc.ReadLeaderTarget(view, rayView, end);
        private static XYZ Origin(Document doc, View view, XYZ end, bool backOff3D) => end;
        private static LeaderHit ReadPlanTarget(Document doc, ViewPlan view, XYZ end, PlanLinkGraphics graphics)
        {
            if (doc.TestPlanCandidates != null)
                return ReadPlanEvidence<PlanProbeCandidate>(Rid.Value(view.Id), doc.TestPlanRegionId,
                    () => (doc.TestPlanCandidates(view, end), () => "no plan target"),
                    c => c.Earliest, c => c.Measure(), c => $"element {c.ElementId}", c => c.Evidence());
            LeaderHit observed = doc.ReadLeaderTarget(view, null, end);
            if (doc.TestExport2D != null)
                return PlanLeaderRules.WithLinkedStrokeFallback(observed, observed.Coverage == "complete", observed.Source != null,
                    () => ReadLinkedStrokeTarget(doc, view, end, graphics, observed));
            string reason = doc.TestUnmeasuredCutLink?.Invoke();
            return reason == null ? observed : LeaderHit.Partial(reason, true, observed);
        }
        public static IList<NoteLeaderRules.Layer> Layers(Document doc, HostObjAttributes type) => throw new NotSupportedException();
    }

    internal sealed record PlanProbeCandidate(long ElementId, double Earliest,
        Func<(double? Distance, bool AtCut, string Problem)> Measure, Func<LeaderHit> Evidence);

    public sealed partial class AnnotateCommand
    {
        private sealed class Plan
        {
            public bool TagLayoutFallback = false;
            public bool TargetViewIsActive;
            public System.Collections.Generic.List<Plan> LayoutBatch;
            public int Index;
            public string Operation, Text;
            public JObject Input = new JObject(), TagCoverage = null;
            public ElementId Created;
            public Element Type;
            public View View;
            public TextLayout TextPlan;
            public XYZ PlannedEnd;
            public bool Constructible;
            public JObject TagRehearsal;
            public ElementType EffectiveTagType;
            public string RoomTagFamilyPath => null;
            public string RoomTagFamilyHash => null;
            public string RoomTagFamilyName => null;
            public string RoomTagTypeName => null;
            public bool RequirePrintable => false;
            public double? ExpectedValueFeet => null;
            public double ExpectedToleranceFeet => 0;
            public Measured Rehearsed;
            public AnnotationBudget Budget;
            public Element LinkedElement => null;
            public RevitLinkInstance LinkInstance => null;
            public Element Target => null;
            public int ExistingTagCount => 0;
            public List<Element> ExtraTargets => null;
            public RefArray References => null;
            public Element EffectiveType => null;
            public bool TypeFromDefault => false;
            public List<string> RefList => throw new NotSupportedException();
            public List<string> RefReserialized => throw new NotSupportedException();
            public List<string> RefOwnerUids => throw new NotSupportedException();
            public List<string> RefTypes => throw new NotSupportedException();
            public List<string> RefFingerprints => throw new NotSupportedException();
            public List<LinkBinding> RefLinks => throw new NotSupportedException();
            public Line Line => null;
            public XYZ ArcCenter => null;
            public double ArcRadius => 0;
            public XYZ SpotOrigin => null;
            public XYZ SpotBend => null;
            public XYZ SpotEnd => null;
            public bool SpotLeader => false;
            public JObject PatternResolutions => null;
        }

        private sealed class RefArray { public int Size => throw new NotSupportedException(); }

        private sealed class TextLayout
        {
            public bool Any, HasLeader;
            public double? WidthFeet = null;
            public int? ExpectedLines = null;
            public XYZ End;
            public string TargetForm, Side, Attachment = null;
            public long? TargetElementId, TargetLinkedId = null;
            public string PlannedLeaderIdentity;
            public bool RayNotApplicable => false;
            public View3D RayView;
            public LeaderHit Hit;
            public JObject Evidence;
            public string VisibilityWarning = null;
        }

        public string Name => "horizun_annotate";

        // Replace only the native planning/geometry inputs. Execute, Rehearse,
        // PlannedRow, the token store and ApplyConfirmed are the shared implementation.
        private static Plan PlanAction(Document doc, int index, JObject action, double scale, long? activeView,
            out string error, out string unsupportedReason, out string errorCode)
        {
            error = unsupportedReason = errorCode = null;
            var view = (View3D)doc.GetElement(new ElementId(action.Value<long>("view_id")));
            var end = new XYZ(5, 6, 7);
            var layout = new TextLayout { Any = true, HasLeader = true, End = end, RayView = view,
                TargetForm = "linked_element", Side = "auto", TargetLinkedId = action["leader_target"].Value<long>("linked_element_id"),
                Hit = doc.ReadLeaderTarget(view, view, end) };
            CapturePlannedLeaderIdentity(layout);
            return new Plan { Index = index, Operation = action.Value<string>("operation"), Text = action.Value<string>("text"),
                Type = new TextNoteType { Id = new ElementId(9) }, View = view, PlannedEnd = end, TextPlan = layout };
        }

        private static void CalibrateText(Document doc, List<Plan> plans) { }
        private static JObject PlanRow(Plan plan) => new JObject { ["index"] = plan.Index };
        private static string SafePlanIdName(Element element) => element?.UniqueId ?? "";
        private static string Canon(XYZ point) => DimensionPlanRules.CanonicalPoint(point.X, point.Y, point.Z);
        private static Measured Measure(Document doc, Plan p, Element element) => throw new NotSupportedException();
        private static JObject BuildChecks(Plan p, Measured first, Measured second, VerifyStage stage, out bool ok) => throw new NotSupportedException();
        private static DimensionPrintRules.Verdict PrintVerdict(Measured measurement, bool unused) => throw new NotSupportedException();
        private static JObject Evidence(Measured measurement) => throw new NotSupportedException();
        private static void MarkEmptyTagText(JObject row, Plan p, string text, bool read) => throw new NotSupportedException();
        private static class AnnotationLayout
        {
            public static JObject Evidence(IndependentTag tag, View view) => throw new NotSupportedException();
        }

        private static class DimensionPaper
        {
            public static JObject VerdictJson(DimensionPrintRules.Verdict verdict) => throw new NotSupportedException();
        }

        private static class LoadTagFamilyEdit
        {
            public static CommandResult Run(UIApplication app, string request, string tool) => throw new NotSupportedException();
        }

        private sealed class Measured
        {
            public string Unreadable => null;
            public string DroppedMessage => null;
            public double? Total => null;
            public bool? ReferencesAvailable => null;
            public List<double> SegmentValues => null;
            public double[] SpotOrigin => null;
        }

        internal CommandResult ApplyTextNotes(Document doc, View view, View3D rayView, bool bestEffort, bool pointOnly, params XYZ[] ends)
            => ApplyTextNotes(doc, view, rayView, bestEffort, pointOnly, global::Horizun.Core.Tests.TextNoteVerificationTests.HostFace(42), ends);

        internal CommandResult ApplyTextNotes(Document doc, View view, View3D rayView, bool bestEffort, bool pointOnly, LeaderHit plannedHit, params XYZ[] ends)
        {
            var plans = ends.Select((end, index) => new Plan
            {
                Index = index, Operation = "text", Text = "Door note", Type = new TextNoteType { Id = new ElementId(7) },
                View = view, PlannedEnd = end,
                TextPlan = new TextLayout { TargetElementId = !pointOnly && plannedHit.Source == "host" ? plannedHit.ElementId : (long?)null,
                    TargetLinkedId = !pointOnly && plannedHit.Source == "link" ? plannedHit.ElementId : (long?)null, RayView = rayView,
                    Hit = plannedHit, PlannedLeaderIdentity = pointOnly ? null : plannedHit.TargetIdentity }
            }).ToList();
            var rehearsal = new Rehearsal { Rows = new JArray(plans.Select(plan => new JObject { ["index"] = plan.Index })) };
            return ApplyConfirmed(doc, "annotate", plans, bestEffort, plans, rehearsal);
        }

        private static Element Create(Document doc, Plan p)
        {
            var note = new TextNote
            {
                Document = doc, Id = new ElementId(doc.NextElementId++), Text = p.Text,
                TextNoteType = (TextNoteType)p.Type, StoredEnd = p.PlannedEnd
            };
            doc.Elements.Add(note.Id.Value, note);
            doc.TestAfterAnnotationCreation?.Invoke();
            return note;
        }

        private static bool ReadTextLayout(Plan p, TextNote note, out Leader leader, out string reason)
        {
            leader = new Leader { End = note.StoredEnd };
            bool endMatch = p.PlannedEnd.DistanceTo(note.StoredEnd) * 304.8 <= NoteLeaderRules.LeaderEndToleranceMm;
            reason = endMatch ? null : "the leader end is outside tolerance";
            p.TextPlan.Evidence = new JObject { ["leader"] = new JObject { ["hit"] = HitJson(p.TextPlan), ["end_match"] = endMatch } };
            return endMatch;
        }

        private static JObject IdNameJson(Element e) => new JObject { ["id"] = e.Id?.Value, ["name"] = e.Name };

        private static void Decorate(Dimension dimension, Plan p) => throw new NotSupportedException();
        private static bool VerifyOtherAnnotation(Plan p, Element e, out string reason) => throw new NotSupportedException();
        private static JObject VerifyDimensionRow(Document doc, Plan p, Element e, VerifyStage stage, out bool ok)
            => throw new NotSupportedException();
        private static void AddTagEvidence(JObject row, Plan p, Element e, ref bool ok) { }
        private static HorizontalTextAlignment ParseHorizontal(string alignment) => throw new NotSupportedException();
        private static VerticalTextAlignment ParseVertical(string alignment) => throw new NotSupportedException();
    }
}
