using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class PlanimetryLeaderReadTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void A_cut_in_the_real_leader_reader_keeps_completed_rows(bool staleness, bool cancel)
        {
            var doc = new Document { TestFallbackRayView = new View3D() };
            doc.Elements.Add(10, new View3D { Id = new ElementId(10) });
            foreach (int id in new[] { 20, 21, 22 })
                doc.Elements.Add(id, new TextNote { Id = new ElementId(id), TestOwnerView = 10, StoredEnd = new XYZ(id, 2, 3) });

            int rays = 0;
            doc.ReadLeaderTarget = (_, _, _) =>
            {
                rays++;
                return TextNoteVerificationTests.HostFace(42);
            };
            var budget = new CooperativeRead.Scope("query_planimetry", 1000, 0,
                elapsedMilliseconds: () => !cancel && rays >= 2 ? 1000 : 0);
            var request = new JObject { ["view_ids"] = new JArray(10) };
            var oldAbandoned = CooperativeRead.Abandoned;
            CooperativeRead.Abandoned = () => cancel && rays >= 2;
            JObject result;
            try
            {
                if (staleness)
                {
                    request["changed_unique_ids"] = new JArray("host-42");
                    result = QueryPlanimetryCommand.ReadStalenessForTest(doc, request, budget);
                }
                else result = QueryPlanimetryCommand.ReadTargetsForTest(doc, request, budget);
            }
            finally { CooperativeRead.Abandoned = oldAbandoned; }

            Assert.Equal(2, rays);
            Assert.True(result.Value<bool>("partial"));
            Assert.False(result.Value<bool>("coverage_complete"));
            Assert.Equal(cancel ? "client_abandoned" : "ui_budget_exhausted", (string)result["partial_reason"]);
            Assert.Equal(staleness ? new[] { 20 } : new[] { 20, 21 }, result["rows"].Select(row => row.Value<int>("note_id")));
        }

        [Theory]
        [InlineData(true, true, false, true)]
        [InlineData(false, true, false, true)]
        [InlineData(true, false, true, true)]
        [InlineData(true, false, false, false)]
        public void VIN13_export_callbacks_and_leader_targets_require_proven_link_identity(
            bool inLinkScope, bool reportsInstance, bool reportsLinkedDocument, bool expectedLink)
        {
            const long viewId = 1428366, noteId = 1428388, elementId = 892350, linkId = 1364712;
            const double mm = 304.8;
            var tip = new XYZ(6750 / mm, 5500 / mm, 0);
            var host = new Document { ReadLeaderTarget = (_, _, _) => new LeaderHit { Cast = true, Coverage = "complete" } };
            var linked = new Document();
            var view = new ViewPlan { Id = new ElementId(viewId), Document = host };
            var transform = new Transform { TestTranslation = new XYZ(1000 / mm, 2000 / mm, 0) };
            var link = new RevitLinkInstance { Id = new ElementId(linkId), Linked = linked, TestTypeId = 1364708,
                TestTotalTransform = transform, TestUniqueId = "m-placement" };
            FamilyInstance Fixture(Document document, long id, string uid) =>
                new FamilyInstance { Id = new ElementId(id), Document = document, TestUniqueId = uid, TestCategory = BuiltInCategory.OST_PlumbingFixtures };
            var linkedFixture = Fixture(linked, elementId, "linked-fixture");
            linked.Elements.Add(elementId, linkedFixture);
            host.Elements.Add(elementId, Fixture(host, elementId, "host-collision"));
            host.Elements.Add(linkId, link);
            host.TestLinkDocuments.Add(link);
            host.Elements.Add(viewId, view);
            host.Elements.Add(noteId, new TextNote { Id = new ElementId(noteId), TestOwnerView = viewId, StoredEnd = tip });

            host.TestExport2D = (output, exportedView) =>
            {
                Assert.Equal(viewId, exportedView.Value);
                // An unrelated host stroke means this output has measurable content
                // even when the node at the tip cannot be attributed.
                var anchor = new ElementNode(host, new ElementId(elementId), ElementId.InvalidElementId);
                output.OnElementBegin2D(anchor);
                output.OnLineSegment(new LineSegment(XYZ.Zero, new XYZ(1, 0, 0)));
                output.OnElementEnd2D(anchor);
                var scope = new LinkNode(linked, link.GetTypeId(), transform);
                if (inLinkScope) output.OnLinkBegin(scope);
                var node = new ElementNode(reportsLinkedDocument ? linked : host, new ElementId(elementId),
                    reportsInstance ? link.Id : ElementId.InvalidElementId);
                output.OnElementBegin2D(node);
                XYZ localTip = inLinkScope ? tip - transform.TestTranslation : tip;
                var half = new XYZ(200 / mm, 0, 0);
                output.OnPolylineSegments(new PolylineSegments(new[] { localTip - half, localTip, localTip + half }));
                output.OnElementEnd2D(node);
                if (inLinkScope) output.OnLinkEnd(scope);
            };

            var graphics = new LeaderTargetReader.PlanLinkGraphics(view);
            if (expectedLink)
                Assert.Equal(0, graphics.Distance(link, linkedFixture, tip), 8);
            else
                Assert.Throws<InvalidOperationException>(() => graphics.Distance(link, linkedFixture, tip));

            JObject response = QueryPlanimetryCommand.ReadTargetsForTest(host, new JObject { ["note_ids"] = new JArray(noteId) });
            Assert.Equal("leader_targets", response.Value<string>("mode"));
            JToken row = Assert.Single(response["rows"]);
            Assert.Equal(noteId, row.Value<long>("note_id"));
            JToken target = row["target"];
            Assert.Equal(expectedLink, target.Value<bool>("hit"));
            Assert.Equal(expectedLink ? "complete" : "partial", target.Value<string>("coverage"));
            if (expectedLink)
            {
                Assert.Equal("link", target.Value<string>("source"));
                Assert.Equal(linkId, target.Value<long>("link_instance_id"));
                Assert.Equal(elementId, target.Value<long>("element_id"));
                Assert.Equal(linkedFixture.UniqueId, target.Value<string>("unique_id"));
            }
            else
            {
                Assert.Null(target.Value<string>("source"));
                Assert.Null(target["link_instance_id"]);
                Assert.Null(target["element_id"]);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Unreadable_linked_stroke_outside_the_door_box_leaves_the_later_hit_uncertain(bool hasDoor)
        {
            var linked = new Document();
            var link = new RevitLinkInstance { Id = new ElementId(51), Linked = linked };
            var end = new XYZ(2, 1, 3);
            if (hasDoor)
                linked.Elements.Add(42, new FamilyInstance
                {
                    Id = new ElementId(42), TestCategory = BuiltInCategory.OST_Doors,
                    BoundingBox = new BoundingBoxXYZ { Min = new XYZ(0, 0, 0), Max = new XYZ(1, 2, 4) }
                });
            var doc = new Document
            {
                // This stroke reaches the tip without an element identity.
                TestExport2D = (output, _) => output.OnLineSegment(new LineSegment(new XYZ(1, 1, 3), new XYZ(3, 1, 3))),
                TestPlanCandidates = (view, tip) => LeaderTargetReader.ProbeLinkedDoorFailureForTest(
                    link, tip, new LeaderTargetReader.PlanLinkGraphics(view))
            };
            doc.Elements.Add(10, new ViewPlan { Id = new ElementId(10), Document = doc });
            doc.Elements.Add(20, new TextNote { Id = new ElementId(20), TestOwnerView = 10, StoredEnd = end });
            doc.Elements.Add(30, new ViewSheet { Id = new ElementId(30) });
            var request = new JObject
            {
                ["view_ids"] = new JArray(10), ["sheet_ids"] = new JArray(30), ["changed_unique_ids"] = new JArray("host-43")
            };

            var targets = QueryPlanimetryCommand.ReadTargetsForTest(doc, request);
            var audit = QueryPlanimetryCommand.ReadStalenessForTest(doc, request);

            JToken target = Assert.Single(targets["rows"])["target"];
            JToken row = Assert.Single(audit["rows"]);
            Assert.True(JToken.DeepEquals(target, row["leader_target"]));
            string coverage = hasDoor ? "partial" : "complete";
            Assert.Equal(coverage, target.Value<string>("coverage"));
            Assert.Equal(coverage, audit["coverage"].Value<string>("ray"));
            Assert.Equal(hasDoor ? 1 : 0, audit.Value<int>("unknown"));
            Assert.Equal(hasDoor ? 0 : 1, audit.Value<int>("stale"));
            Assert.Equal(!hasDoor, target.Value<bool>("hit"));
            if (hasDoor)
            {
                Assert.Contains("nearby linked door strokes could not be verified", target.Value<string>("uncertainty"));
                Assert.Equal(43, target["first_ray_hit"].Value<long>("element_id"));
                Assert.Equal("unknown_not_measured", row.Value<string>("verdict"));
            }
            else Assert.Equal(43, target.Value<long>("element_id"));
        }

        [Theory]
        [InlineData("door", 42)]
        [InlineData("slab", 43)]
        [InlineData("unmeasured_link", 43)]
        [InlineData("plan_region", -1)]
        public void Staleness_uses_the_same_plan_selection_as_leader_targets(string scenario, long first)
        {
            var view = new ViewPlan { Id = new ElementId(10) };
            var note = new TextNote { Id = new ElementId(20), TestOwnerView = 10,
                StoredEnd = new XYZ(1, scenario == "door" ? 2 : 2.05, 3) };
            var doc = new Document { TestFallbackRayView = new View3D() };
            doc.Elements.Add(10, view);
            doc.Elements.Add(20, note);
            if (scenario == "plan_region") doc.TestPlanRegionId = 60;
            doc.ReadLeaderTarget = (_, _, _) => throw new InvalidOperationException("the auxiliary view cannot prove a plan hit");
            doc.TestPlanCandidates = (readView, end) =>
            {
                Assert.Same(view, readView);
                Assert.NotEqual("plan_region", scenario); // The global range must never be probed here.
                double distance = TextNoteLayoutRules.PlanSegmentDistance(end.X, end.Y, .5, 2, 1.5, 2);
                var candidates = new List<PlanProbeCandidate>
                {
                    new PlanProbeCandidate(43, .2, () => (.2, false, null), () => TextNoteVerificationTests.HostFace(43)),
                    new PlanProbeCandidate(42, 0, () => distance <= TextNoteLayoutRules.PlanStrokeToleranceFeet
                        ? (0, true, null) : (null, false, null), () => TextNoteVerificationTests.HostFace(42))
                };
                if (scenario == "unmeasured_link") candidates.Add(new PlanProbeCandidate(51, 0,
                    () => (null, false, "linked geometry at the cut plane was not measured"),
                    () => throw new InvalidOperationException("the link has no verified evidence")));
                return candidates;
            };
            var request = new JObject { ["view_ids"] = new JArray(10), ["changed_unique_ids"] = new JArray("host-42", "host-43") };

            var targets = QueryPlanimetryCommand.ReadTargetsForTest(doc, request);
            doc.Elements.Add(30, new ViewSheet { Id = new ElementId(30) });
            request["sheet_ids"] = new JArray(30);
            var audit = QueryPlanimetryCommand.ReadStalenessForTest(doc, request);

            Assert.False(audit.Value<bool>("clean"));
            Assert.True(audit["coverage"]["finishes_tables"].Value<bool>("complete"));
            bool partial = scenario == "unmeasured_link" || scenario == "plan_region";
            Assert.Equal(partial ? "partial" : "complete", audit["coverage"].Value<string>("ray"));
            Assert.Equal(partial ? 0 : 1, audit.Value<int>("stale"));
            Assert.Equal(partial ? 1 : 0, audit.Value<int>("unknown"));
            JToken target = Assert.Single(targets["rows"])["target"];
            JToken audited = Assert.Single(audit["rows"]);
            Assert.True(JToken.DeepEquals(target, audited["leader_target"]));
            if (scenario == "plan_region")
            {
                Assert.False(target.Value<bool>("hit"));
                Assert.Null(target["first_ray_hit"]);
                Assert.Contains("plan region 60 in view 10", target.Value<string>("uncertainty"));
            }
            else if (partial)
            {
                Assert.Equal("partial", target.Value<string>("coverage"));
                Assert.Equal(first, target["first_ray_hit"].Value<long>("element_id"));
            }
            else
            {
                Assert.Equal(first, target.Value<long>("element_id"));
                Assert.Equal("target_changed", (string)Assert.Single(audited["reasons"]));
            }
        }

        [Theory]
        [InlineData("target", true, 1, 0, "complete", null)]
        [InlineData("partial", false, 0, 1, "partial", "unknown_not_measured")]
        [InlineData("empty", false, 0, 1, "complete", "unknown_no_target")]
        [InlineData("not_cast", false, 0, 1, "partial", "unknown_not_measured")]
        public void Matching_origin_cannot_make_an_unproven_leader_target_clean(
            string reading, bool clean, int current, int unknown, string coverage, string verdict)
        {
            var sourceType = new ElementType { Id = new ElementId(41), Name = "Door type" };
            var source = new TextNote { Id = new ElementId(42), TextNoteType = new TextNoteType { Id = sourceType.Id } };
            var note = new TextNote
            {
                Id = new ElementId(20), TestOwnerView = 10, StoredEnd = new XYZ(1, 2, 3),
                TestOrigin = new NoteOrigin.Record { Source = "type_name", SourceUniqueId = source.UniqueId,
                    Signature = NoteLeaderRules.Signature(source.UniqueId, "type_name", sourceType.Name) }
            };
            var doc = new Document { TestLookupUniqueId = id => id == source.UniqueId ? source : null };
            doc.Elements.Add(10, new ViewPlan { Id = new ElementId(10) });
            doc.Elements.Add(20, note);
            doc.Elements.Add(30, new ViewSheet { Id = new ElementId(30) });
            doc.Elements.Add(41, sourceType);
            doc.ReadLeaderTarget = (_, _, _) => reading == "empty" || reading == "not_cast"
                ? new LeaderHit { Cast = reading == "empty", Coverage = reading == "empty" ? "complete" : "not_cast" }
                : new LeaderHit { Cast = true, Coverage = "complete", Source = "host", ElementId = 42, UniqueId = source.UniqueId };
            doc.TestUnmeasuredCutLink = () => reading == "partial" ? "linked geometry at the cut plane was not measured" : null;
            var request = new JObject { ["view_ids"] = new JArray(10), ["sheet_ids"] = new JArray(30) };

            var audit = QueryPlanimetryCommand.ReadStalenessForTest(doc, request);

            Assert.Equal(clean, audit.Value<bool>("clean"));
            Assert.Equal(current, audit.Value<int>("current"));
            Assert.Equal(unknown, audit.Value<int>("unknown"));
            Assert.Equal(coverage, audit["coverage"].Value<string>("ray"));
            Assert.True(audit["coverage"]["finishes_tables"].Value<bool>("complete"));
            if (clean) Assert.Empty(audit["rows"]);
            else
            {
                var row = Assert.Single(audit["rows"]);
                Assert.Equal(verdict, row.Value<string>("verdict"));
                Assert.Equal(source.UniqueId, row.Value<string>("origin_unique_id"));
                Assert.Equal(reading == "empty" ? "complete" : reading, row["leader_target"].Value<string>("coverage"));
            }
        }

        [Theory]
        [InlineData("current", "complete", null)]
        [InlineData("changed", "complete", "stale")]
        [InlineData("deleted", "complete", "stale")]
        [InlineData("empty", "complete", "unknown_no_target")]
        [InlineData("not_cast", "partial", "unknown_not_measured")]
        [InlineData("partial", "partial", "unknown_not_measured")]
        public void Staleness_classifies_the_note_using_every_leader(string second, string rayCoverage, string verdict)
        {
            var sourceType = new ElementType { Id = new ElementId(41), Name = "Door type" };
            var source = new TextNote { Id = new ElementId(42), TextNoteType = new TextNoteType { Id = sourceType.Id } };
            var ends = new[] { new XYZ(1, 2, 3), new XYZ(2, 2, 3), new XYZ(3, 2, 3) };
            var note = new TextNote
            {
                Id = new ElementId(20), TestOwnerView = 10, TestLeaderEnds = ends,
                TestOrigin = new NoteOrigin.Record { Source = "type_name", SourceUniqueId = source.UniqueId,
                    Signature = NoteLeaderRules.Signature(source.UniqueId, "type_name", sourceType.Name) }
            };
            var doc = new Document { TestFallbackRayView = new View3D(), TestLookupUniqueId = id => id == source.UniqueId ? source : null };
            doc.Elements.Add(10, new View3D { Id = new ElementId(10) });
            doc.Elements.Add(20, note);
            doc.Elements.Add(30, new ViewSheet { Id = new ElementId(30) });
            doc.Elements.Add(41, sourceType);
            var castEnds = new List<XYZ>();
            doc.ReadLeaderTarget = (_, _, end) =>
            {
                castEnds.Add(end);
                if (end == ends[0]) return new LeaderHit { Cast = true, Coverage = "complete", Source = "host", ElementId = 42, UniqueId = source.UniqueId };
                if (end == ends[2]) return TextNoteVerificationTests.HostFace(44);
                if (second == "empty" || second == "not_cast")
                    return new LeaderHit { Cast = second == "empty", Coverage = second == "empty" ? "complete" : "not_cast" };
                return TextNoteVerificationTests.HostFace(43, second == "partial" ? "partial" : "complete");
            };
            var request = new JObject { ["view_ids"] = new JArray(10), ["sheet_ids"] = new JArray(30) };
            if (second == "changed") request["changed_unique_ids"] = new JArray("host-43");
            if (second == "deleted") request["deleted_unique_ids"] = new JArray("host-43");

            var audit = QueryPlanimetryCommand.ReadStalenessForTest(doc, request);

            Assert.Equal(ends, castEnds);
            Assert.Equal(verdict == null, audit.Value<bool>("clean"));
            Assert.Equal(verdict == null ? 1 : 0, audit.Value<int>("current"));
            Assert.Equal(verdict == "stale" ? 1 : 0, audit.Value<int>("stale"));
            Assert.Equal(verdict != null && verdict != "stale" ? 1 : 0, audit.Value<int>("unknown"));
            Assert.Equal(rayCoverage, audit["coverage"].Value<string>("ray"));
            Assert.Equal(1, audit["coverage"].Value<int>("notes_with_leader_target"));
            if (verdict == null) { Assert.Empty(audit["rows"]); return; }
            JToken row = Assert.Single(audit["rows"]);
            Assert.Equal(verdict, row.Value<string>("verdict"));
            Assert.Equal(new[] { 0, 1, 2 }, row["leader_targets"].Select(leader => leader.Value<int>("leader_index")));
            Assert.True(JToken.DeepEquals(row["leader_target"], row["leader_targets"][0]["target"]));
            Assert.Equal(verdict, row["leader_targets"][1].Value<string>("verdict"));
            if (verdict == "stale")
                Assert.Equal("target_" + second, (string)Assert.Single(row["reasons"]));
        }

        [Theory]
        [InlineData("Finish", "Core", false, true)]
        [InlineData("Core", "Finish", false, true)]
        [InlineData("Core", "Insulation", false, false)]
        [InlineData("Core", "Finish", true, false)]
        public void Any_measured_layer_can_confirm_the_stored_material(
            string firstMaterial, string secondMaterial, bool sourceChanged, bool current)
        {
            var source = new Element { Id = new ElementId(42) };
            var firstEnd = new XYZ(1, 2, 3);
            var secondEnd = new XYZ(2, 2, 3);
            var note = new TextNote
            {
                Id = new ElementId(20), TestOwnerView = 10, TestLeaderEnds = new[] { firstEnd, secondEnd },
                TestOrigin = new NoteOrigin.Record { Source = "material", SourceUniqueId = source.UniqueId,
                    Signature = NoteLeaderRules.Signature(source.UniqueId, "material", "Finish") }
            };
            var doc = new Document { TestFallbackRayView = new View3D(), TestLookupUniqueId = id => id == source.UniqueId ? source : null };
            doc.Elements.Add(10, new View3D { Id = new ElementId(10) });
            doc.Elements.Add(20, note);
            doc.Elements.Add(30, new ViewSheet { Id = new ElementId(30) });
            doc.ReadLeaderTarget = (_, _, end) => new LeaderHit
            {
                Cast = true, Coverage = "complete", Source = "host", ElementId = 42, UniqueId = source.UniqueId,
                LayerMaterial = end == firstEnd ? firstMaterial : secondMaterial
            };
            var request = new JObject { ["view_ids"] = new JArray(10), ["sheet_ids"] = new JArray(30) };
            if (sourceChanged) request["changed_unique_ids"] = new JArray(source.UniqueId);

            var audit = QueryPlanimetryCommand.ReadStalenessForTest(doc, request);

            Assert.Equal(current, audit.Value<bool>("clean"));
            Assert.Equal(current ? 1 : 0, audit.Value<int>("current"));
            Assert.Equal(current ? 0 : 1, audit.Value<int>("stale"));
            Assert.Equal(0, audit.Value<int>("unknown"));
            if (current) Assert.Empty(audit["rows"]);
            else Assert.Equal(sourceChanged ? "origin_changed" : "text_out_of_date", (string)Assert.Single(Assert.Single(audit["rows"])["reasons"]));
        }
    }
}

namespace Autodesk.Revit.DB
{
    public class ScheduleSheetInstance : Element { public ElementId ScheduleId => throw new NotSupportedException(); }

    public partial class TextNote
    {
        internal Horizun.Revit.Commands.NoteOrigin.Record TestOrigin;
        internal XYZ[] TestLeaderEnds;
        public IList<Leader> GetLeaders() => (TestLeaderEnds ?? new[] { StoredEnd }).Select(end => new Leader { End = end }).ToList();
    }
}

namespace Horizun.Revit.Commands
{
    internal static partial class LeaderTargetReader
    {
        internal static IReadOnlyList<PlanProbeCandidate> ProbeLinkedDoorFailureForTest(
            RevitLinkInstance link, XYZ tip, PlanLinkGraphics graphics)
        {
            var candidates = new List<PlanProbeCandidate>
            {
                new PlanProbeCandidate(43, .2, () => (.2, false, null), () => Horizun.Core.Tests.TextNoteVerificationTests.HostFace(43))
            };
            ReadLinkedDoorStrokes(link, link.Linked, tip, graphics, out string problem);
            if (problem != null)
                candidates.Add(new PlanProbeCandidate(link.Id.Value, 0, () => (null, false, problem),
                    () => throw new InvalidOperationException("the link has no verified evidence")));
            return candidates;
        }
    }

    public sealed partial class QueryPlanimetryCommand
    {
        internal static JObject ReadTargetsForTest(Document doc, JObject request, CooperativeRead.Scope budget = null) =>
            ReadLeaderRowsForTest(budget, rows => LeaderTargets(doc, request, 10, rows));

        internal static JObject ReadStalenessForTest(Document doc, JObject request, CooperativeRead.Scope budget = null)
        {
            const string mode = "annotation_staleness";
            Assert.Null(NoteLeaderRules.ValidateRead(mode, request));
            Assert.All(request.Properties(), property => Assert.True(NoteLeaderRules.NoteReadFieldAllowed(mode, property.Name)));
            return ReadLeaderRowsForTest(budget, rows => Staleness(doc, request, 10, rows));
        }

        private static JObject ReadLeaderRowsForTest(CooperativeRead.Scope budget, Func<JArray, CommandResult> read) =>
            (JObject)(budget == null ? read(new JArray()) : PlanimetryReadBudget.Read(budget, read,
                rows => new JObject { ["rows"] = rows })).Data;
        private const double FtPerMm = 1.0 / 304.8;
        private static JArray Mm3(XYZ p) => new JArray(p.X / FtPerMm, p.Y / FtPerMm, p.Z / FtPerMm);
        private static T NeedAs<T>(Document doc, long id, string kind) where T : Element => (T)doc.GetElement(new ElementId(id));
    }

    internal static class NoteOrigin
    {
        internal sealed class Record
        {
            public string Source { get; set; }
            public string SourceUniqueId { get; set; }
            public string LinkInstanceUniqueId { get; set; }
            public string Signature { get; set; }
        }
        public static Record Read(TextNote note) => note.TestOrigin;
    }

    internal static class ScheduleAssociationStorage
    {
        public static ScheduleAssociation.Record Read(TextNote note) => null;
    }

    internal static class ScheduleSource
    {
        public static string Read(ViewSchedule schedule, bool coverageRead = false) => throw new NotSupportedException();
        public static string Hash(ViewSchedule schedule, string body) => throw new NotSupportedException();
    }
}
