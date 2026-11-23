using System;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanLeaderGeometryTests
    {
        [Fact]
        public void VIN13_resolves_the_recorded_link_without_confusing_document_wrappers_or_placements()
        {
            const long link = 1364712, element = 142552;
            const double mm = 304.8;
            var host = new DocumentWrapper("Casa-vinculos");
            var linked = new DocumentWrapper("Casa Horizun-M");
            var callback = new DocumentWrapper(linked.Path);
            long? scoped = PlanLeaderRules.UniqueExportedLinkInstance(new[] { link });
            long? instance = PlanLeaderRules.ExportedStrokeInstance(-1, 1, scoped);
            Assert.Equal(link, instance);
            var owner = PlanLeaderRules.ExportedStrokeDocument(host, callback, instance.Value, element,
                id => id == link ? linked : null, (doc, id) => ReferenceEquals(doc, linked) && id == element,
                (a, b) => a.Path == b.Path);
            Assert.Same(linked, owner);
            Assert.Equal(link, PlanLeaderRules.ExportedStrokeInstance(link, 0, null));
            Assert.Null(PlanLeaderRules.ExportedStrokeInstance(link + 1, 1, scoped));
            Assert.Null(PlanLeaderRules.ExportedStrokeInstance(link, 2, scoped));
            Assert.Null(PlanLeaderRules.UniqueExportedLinkInstance(new[] { link, link + 1 }));
            Assert.Null(PlanLeaderRules.ExportedStrokeInstance(-1, 1, null));

            var index = new PlanStrokeIndex<DocumentWrapper>();
            // Synthetic stroke centred at the recorded first note placement in host coordinates.
            var arrow = new CadPoint(5915.03492552758 / mm, -3192.2880398668312 / mm, 0);
            var start = new CadPoint(arrow.X - 1, arrow.Y, 1);
            var end = new CadPoint(arrow.X + 1, arrow.Y, 1);
            index.Add(owner, element, instance.Value, true, start, end);
            Assert.Equal((linked, element, link), index.UniqueLinkedStroke(arrow, out bool ambiguous));
            Assert.False(ambiguous);
            index.Add(linked, element, link + 1, true, start, end);
            Assert.Null(index.UniqueLinkedStroke(arrow, out ambiguous));
            Assert.True(ambiguous);
        }

        private sealed class DocumentWrapper
        {
            internal string Path { get; }
            internal DocumentWrapper(string path) { Path = path; }
        }

        [Fact]
        public void VIN13_hidden_line_output_resolves_a_linked_id_even_when_the_node_names_the_host()
        {
            const long elementId = 312;
            const long linkInstance = 901;
            const string host = "Casa-vinculos", mep = "Casa Horizun-M";
            var strokes = new PlanStrokeIndex<string>();
            string Owner(string reported, long instance) => PlanLeaderRules.ExportedStrokeDocument(host, reported,
                instance, elementId, id => id == linkInstance ? mep : null,
                (document, id) => (document == host || document == mep) && id == elementId);
            var start = new CadPoint(10, 3, 2);
            var end = new CadPoint(12, 3, 2);

            string owner = Owner(host, linkInstance);
            Assert.Equal(mep, owner);
            Assert.Equal(mep, Owner(mep, linkInstance));
            Assert.Null(Owner("nested model", linkInstance));
            Assert.Null(Owner(host, 902));
            strokes.Add(owner, elementId, linkInstance, true, start, end);
            CadPoint aim = strokes.AimPoints(mep, elementId, linkInstance).First();
            var measured = strokes.UniqueLinkedStroke(new CadPoint(aim.X, aim.Y, 0), out bool ambiguous);
            Assert.False(ambiguous);
            Assert.Equal((mep, elementId, linkInstance), measured);
            Assert.Empty(strokes.AimPoints(host, elementId, -1));

            // The same host element ID on a coincident stroke still blocks attribution.
            strokes.Add(Owner(host, -1), elementId, -1, true, start, end);
            Assert.Null(strokes.UniqueLinkedStroke(aim, out ambiguous));
            Assert.True(ambiguous);
        }

        private sealed record Point(double X, double Y, double Z = 0);
        private sealed record Target(long ElementId, long InstanceId, double Earliest = 0);
        private sealed record Hit(Target Target, string Coverage);

        [Fact]
        public void VIN13_requires_a_local_drawn_segment_owned_by_the_exact_transformed_instance()
        {
            var linked = new Target(71, 201);
            var secondInstance = new Target(71, 202);
            var floor = new Target(71, -1, .5);
            var output = new PlanStrokeIndex<string>();
            // One duct is rotated 90 degrees and translated, with an occluded gap between drawn segments.
            output.Add("mep", 71, 201, true, new CadPoint(98, 21, 4), new CadPoint(98, 22, 4));
            output.Add("mep", 71, 201, true, new CadPoint(98, 24, 4), new CadPoint(98, 25, 4));
            output.Add("mep", 71, 202, true, new CadPoint(108, 22, 4), new CadPoint(108, 24, 4));
            output.Add("other", 71, 201, true, new CadPoint(98, 22, 4), new CadPoint(98, 24, 4));
            output.Add("mep", 71, 201, true, new CadPoint(98, 23, 4), new CadPoint(98, 23, 5)); // collapses in plan

            (double?, bool, string) Measure(Target candidate, Point point)
            {
                if (candidate == floor) return (.5, false, null);
                double offset = candidate == linked ? 100 : 110;
                Point InLink(Point p) => new Point(p.Y - 20, offset - p.X, p.Z - 3);
                var probe = TextNoteLayoutRules.ProbeInElementSpace(point with { Z = point.Z + .01 },
                    point with { Z = point.Z - .01 }, InLink);
                bool InDuct(Point p) => p.X >= 1 && p.X <= 5 && p.Y >= 2 && p.Y <= 2.1 && p.Z > .9 && p.Z < 1.1;
                if (!InDuct(probe.Start) || !InDuct(probe.End)) return (null, false, null);
                double? distance = output.Distance("mep", candidate.ElementId, candidate.InstanceId,
                    new CadPoint(point.X, point.Y, point.Z));
                if (!distance.HasValue) return (0, false, "unattributed segment");
                return distance <= TextNoteLayoutRules.PlanStrokeToleranceFeet
                    ? ((double?)0, true, null) : (0, false, "no local drawn segment");
            }

            Hit Read(Point point)
            {
                var result = PlanLeaderRules.FirstTarget(new[] { floor, secondInstance, linked }, t => t.Earliest,
                    t => Measure(t, point), t => t.ToString());
                return new Hit(result.Target, result.Problem == null ? "complete" : "partial");
            }
            bool Confirms(Hit hit) => TextNoteLayoutRules.ConfirmsTarget(true, hit.Coverage, hit.Target == linked);
            var covered = new Point(98, 23, 4);
            var samples = output.AimPoints("mep", 71, 201).Select(p => new Point(p.X, p.Y, p.Z));
            Point aim = TextNoteLayoutRules.FirstAim(new[] { covered }.Concat(samples), Read, Confirms, out Hit rehearsal);

            Assert.Equal("partial", Read(covered).Coverage);
            Assert.Equal(new Point(98, 21.5, 4), aim);
            Assert.Equal(linked, rehearsal.Target);
            Assert.True(Confirms(Read(aim)));
            Assert.Equal(secondInstance, Read(new Point(108, 23, 4)).Target);
            Assert.False(Confirms(Read(new Point(98, 20.99, 4)))); // extension of the visible stroke
            output.Add("mep", 71, -1, false, new CadPoint(98, 22, 4), new CadPoint(98, 24, 4));
            Assert.Equal("partial", Read(covered).Coverage);
            Assert.True(Confirms(Read(aim))); // unidentifiable output elsewhere does not invalidate this point
        }

        [Fact]
        public void A_linked_door_symbol_can_win_without_a_solid_intersection()
        {
            var strokes = new PlanStrokeIndex<string>();
            CadPoint InHost(double x, double y) => new CadPoint(90 - y, 27 + x, 8);
            strokes.Add("building", 42, 201, true, InHost(1, 2), InHost(3, 2));
            strokes.Add("building", 42, 202, true, InHost(3, 2), InHost(5, 2));
            strokes.Add("other", 42, 201, true, InHost(3, 2), InHost(5, 2));
            var slab = new Target(43, -1, .2);
            var door = new Target(42, 201);

            Target Read(CadPoint point)
            {
                var drawn = strokes.NearbyElements("building", 201, point)
                    .Select(stroke => new Target(stroke.ElementId, 201));
                return PlanLeaderRules.FirstTarget(drawn.Append(slab), t => t.Earliest,
                    t => t == slab ? (.2, false, null) : ((double?)0, true, null), t => t.ToString()).Target;
            }

            CadPoint end = InHost(2, 2);
            Assert.Equal(door, Read(end));
            Assert.Equal(door, Read(new CadPoint(end.X, end.Y, 0))); // stored note plane, away from the model solid
            Assert.Equal(slab, Read(InHost(4, 2))); // extension and same element ID in another instance and document
            Assert.Equal(slab, Read(InHost(2, 2.001)));
            strokes.Add("building", 42, -1, false, InHost(1, 2), InHost(3, 2));
            Assert.Throws<InvalidOperationException>(() => Read(end));
        }

        [Fact]
        public void DOC24_hits_the_door_leaf_in_plan_and_refuses_a_moved_or_ambiguous_end()
        {
            var door = new Target(42, -1);
            var wall = new Target(43, -1);
            var floor = new Target(44, -1, .2);
            bool wallMoved = false;
            var leaf = new Point(.8, .4);
            var extension = new Point(1.4, .4);

            Hit Read(Point end)
            {
                var result = PlanLeaderRules.FirstTarget(new[] { floor, wall, door }, t => t.Earliest, t =>
                {
                    if (t == floor) return (.2, false, null);
                    bool atCut = t == door
                        ? TextNoteLayoutRules.PlanSegmentDistance(end.X, end.Y, .2, .4, 1.2, .4) <= TextNoteLayoutRules.PlanStrokeToleranceFeet
                        : wallMoved && Math.Abs(end.Y - .4) < .01;
                    return (atCut ? (double?)0 : null, atCut, null);
                }, t => t.ToString());
                return new Hit(result.Target, result.Problem == null ? "complete" : "partial");
            }

            bool Confirms(Hit hit) => TextNoteLayoutRules.ConfirmsTarget(true, hit.Coverage, hit.Target == door);
            Point aim = TextNoteLayoutRules.FirstAim(new[] { new Point(.8, .3), extension, leaf }, Read, Confirms, out Hit rehearsal);
            Assert.Equal(leaf, aim);
            Assert.Equal(door, rehearsal.Target);
            Assert.True(Confirms(Read(aim)));
            Assert.Equal(floor, Read(aim with { Y = .401 }).Target);
            Assert.False(Confirms(Read(extension)));
            wallMoved = true;
            Assert.Equal("partial", Read(aim).Coverage);
            Assert.False(Confirms(Read(aim)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_later_unreadable_face_cannot_discard_a_verified_plan_hit(bool cut)
        {
            var first = new Target(1, -1);
            var later = new Target(2, -1, .6);
            var proxy = new Target(3, -1, .8);
            var result = PlanLeaderRules.FirstTarget(new[] { proxy, later, first }, t => t.Earliest,
                t => t == first ? (cut ? 0 : .2, cut, null) :
                    throw new InvalidOperationException("a later face cannot affect the first plan impact"),
                t => t.ToString());

            Assert.Equal(first, result.Target);
            Assert.Null(result.Problem);
        }

        [Fact]
        public void Measured_depth_of_an_uncertain_face_can_rule_it_out_even_when_its_box_reaches_the_cut()
        {
            var unknown = new Target(1, 201);
            var first = new Target(2, -1, .1);
            var result = PlanLeaderRules.FirstTarget(new[] { unknown, first }, t => t.Earliest,
                t => t == unknown ? (.6, false, "face visibility could not be measured") : (.2, false, null),
                t => t.ToString());

            Assert.Equal(first, result.Target);
            Assert.Null(result.Problem);
        }

        [Theory]
        [InlineData(-.4, -.1, null)]
        [InlineData(-.4, .1, 0.0)]
        [InlineData(.2, .1, .1)]
        [InlineData(.8, .9, null)]
        public void A_face_from_the_plan_is_measured_from_cut_to_depth(double a, double b, double? first)
            => Assert.Equal(first, PlanLeaderRules.EntryInRange(a, b, .5));

        [Fact]
        public void Point_and_concave_crop_vertices_use_the_same_projection_frame()
        {
            Point ToModel(double x, double y) => new Point(90 - y, 27 + x, 4);
            CadPoint ToView(Point p) => new CadPoint(p.Y - 27, 90 - p.X, 0);
            Point[] boundary = { ToModel(0, 0), ToModel(4, 0), ToModel(4, 1),
                ToModel(1, 1), ToModel(1, 3), ToModel(0, 3) };
            Assert.True(PlanLeaderRules.InsideCrop(boundary, ToModel(.5, 2) with { Z = 8 }, ToView));
            Assert.True(PlanLeaderRules.InsideCrop(boundary, ToModel(4, .5), ToView));
            Assert.False(PlanLeaderRules.InsideCrop(boundary, ToModel(2, 2), ToView));
        }

        [Theory]
        [InlineData(0, "No geometry or bounding box")]
        [InlineData(3, "None of the tested points")]
        public void Aim_diagnostic_distinguishes_empty_input_from_sampled_misses(int count, string expected)
            => Assert.StartsWith(expected, TextNoteLayoutRules.AimMissDetail(count));
    }
}
