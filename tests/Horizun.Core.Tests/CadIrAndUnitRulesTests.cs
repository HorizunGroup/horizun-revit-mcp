// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// TWO PROMISES, AND BOTH OF THEM ARE ABOUT NOT MISLEADING SOMEBODY WITH TRUE NUMBERS.
//
// THE IR's promise: an empty list means two different things and the difference
// must survive. "This drawing has no text" is a finding about the building.
// "This reader cannot see text" is a finding about the reader, and it is the one
// that says go and get a different reader. Both compile to Count == 0, so only a
// test keeps them apart — and the pessimistic default (an axis nobody declared is
// an axis nobody can be trusted on) is the one that has to hold when somebody
// adds a thirteenth axis and forgets to declare it.
//
// THE UNIT RECOGNISER's promise: it never turns "these look alike" into "these
// are the same". Two apartments that differ by one wall are two apartments, and a
// recogniser that scores them at 96% and proceeds will put one unit's outlets in
// another's walls, on thirty floors, identically.
//
// NOT RUN in this phase. Written to be run when running tests is authorised.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadIrTests
    {
        private static CadReaderCapability Thin() =>
            new CadReaderCapability("test-reader", "1.0", "a reader that can see lines and nothing else")
                .Declare(CadAxes.Geometry, CadAxisState.Supplied, "every curve was walked", 4)
                .Declare(CadAxes.Layers, CadAxisState.Supplied, "layer names come off the graphics style", 2)
                .Declare(CadAxes.Text, CadAxisState.Unavailable,
                         "MEASURED: not one reachable string at any depth");

        [Fact]
        public void An_axis_the_reader_is_blind_to_is_not_usable()
        {
            CadReaderCapability c = Thin();
            Assert.True(c.Can(CadAxes.Geometry));
            Assert.False(c.Can(CadAxes.Text));
        }

        [Fact]
        public void An_axis_nobody_declared_is_treated_as_one_the_reader_cannot_supply()
        {
            // THE PESSIMISTIC DEFAULT. A reader that forgets to declare an axis
            // must not be assumed capable of it: the alternative is a rule that
            // silently matches nothing and a report saying the drawing was empty.
            CadReaderCapability c = Thin();
            Assert.False(c.Can(CadAxes.BlockAttributes));
            Assert.Contains(CadAxes.BlockAttributes, c.Undeclared);

            JObject refusal = c.RefusalFor(CadAxes.BlockAttributes, "reading panel names");
            Assert.Equal("undeclared", refusal.Value<string>("state"));
            Assert.Contains("pessimistic", refusal.Value<string>("evidence"));
        }

        [Fact]
        public void Absent_and_unavailable_are_different_answers_with_different_sentences()
        {
            var seen = new CadReaderCapability("rich", "1.0", "sees everything")
                .Declare(CadAxes.Text, CadAxisState.Absent, "the reader reads text and this drawing has none", 0);
            var blind = Thin();

            string absentMeans = seen.Axis(CadAxes.Text).ToJson().Value<string>("means");
            string blindMeans = blind.Axis(CadAxes.Text).ToJson().Value<string>("means");

            Assert.Contains("this IS a finding", absentMeans);
            Assert.Contains("NOTHING IS KNOWN", blindMeans);
            Assert.NotEqual(absentMeans, blindMeans);

            // And only one of them lets a rule run.
            Assert.True(seen.Can(CadAxes.Text));
            Assert.False(blind.Can(CadAxes.Text));
        }

        [Fact]
        public void An_ir_refuses_a_rule_that_needs_an_axis_its_reader_lacks()
        {
            var ir = new CadIr { Reader = Thin(), SourceName = "DEMO-UNITS.dwg" };
            Assert.Null(ir.RefuseIfBlind(CadAxes.Geometry, "reading the runs"));

            JObject no = ir.RefuseIfBlind(CadAxes.Text, "naming the rooms");
            Assert.NotNull(no);
            Assert.Equal("reader_cannot_supply_axis", no.Value<string>("refused"));
            Assert.Equal("naming the rooms", no.Value<string>("needed_by"));
            Assert.Contains("nothing was looked at", no.Value<string>("means"));
        }

        [Fact]
        public void An_ir_with_no_reader_refuses_everything_that_depends_on_an_axis()
        {
            var ir = new CadIr { Reader = null };
            JObject no = ir.RefuseIfBlind(CadAxes.Geometry, "anything at all");
            Assert.Equal("reading_declares_no_reader", no.Value<string>("refused"));
        }

        [Fact]
        public void Every_count_travels_with_the_sentence_that_says_which_kind_of_zero_it_is()
        {
            var ir = new CadIr { Reader = Thin() };
            string caveat = ir.CountsCaveat();
            Assert.Contains("test-reader", caveat);
            Assert.Contains(CadAxes.Text, caveat);
            Assert.Contains("not the drawing", caveat);
            Assert.Equal(caveat, ir.SummaryJson().Value<string>("counts_mean"));
        }

        [Fact]
        public void A_reading_that_lost_nothing_says_so_plainly()
        {
            var full = new CadReaderCapability("complete", "1.0", "")
                .Declare(CadAxes.Geometry, CadAxisState.Supplied, "all of it", 3)
                .Declare(CadAxes.Text, CadAxisState.Absent, "readable, and there is none", 0);
            var ir = new CadIr { Reader = full };
            Assert.Contains("every axis this reader declared was supplied", ir.CountsCaveat());
        }

        [Fact]
        public void Partial_counts_are_declared_lower_bounds()
        {
            var partial = new CadReaderCapability("bounded", "1.0", "")
                .Declare(CadAxes.Geometry, CadAxisState.Partial, "the walk stopped at its bound", 200000);
            var ir = new CadIr { Reader = partial };
            Assert.Contains("lower bounds", ir.CountsCaveat());
        }

        [Fact]
        public void An_axis_verdict_without_evidence_cannot_be_built_at_all()
        {
            // An assertion with no reason behind it is the shape this whole file
            // exists to prevent, so the constructor refuses to make one.
            Assert.Throws<ArgumentException>(() =>
                new CadAxis(CadAxes.Text, CadAxisState.Unavailable, "   "));
        }

        [Fact]
        public void A_layers_entity_count_and_its_primitive_count_are_kept_apart()
        {
            // THE DEFECT THIS PINS. The per-layer number used to be the count of
            // GeometryObjects the walk VISITED while the total was the count of
            // entities it PRODUCED, so the parts did not add up to the whole - on
            // exactly the layers where it matters, the ones carrying solids and
            // hatch residue.
            var layer = new CadIrLayer { Name = "A-WALL", EntityCount = 12, PrimitiveCount = 47 };
            JObject o = layer.ToJson();

            Assert.Equal(12, o.Value<int>("entity_count"));
            Assert.Equal(47, o.Value<int>("primitive_count"));
            Assert.Contains("lost most of what is there", o.Value<string>("counts_differ_means"));
        }

        [Fact]
        public void A_reader_that_does_not_count_primitives_publishes_no_primitive_count()
        {
            // -1 is "not counted", and it must not reach a reader as a number.
            var layer = new CadIrLayer { Name = "A-WALL", EntityCount = 12 };
            JObject o = layer.ToJson();
            Assert.Equal(12, o.Value<int>("entity_count"));
            Assert.Null(o["primitive_count"]);
            Assert.Null(o["counts_differ_means"]);
        }

        [Fact]
        public void Equal_counts_need_no_explanation()
        {
            var layer = new CadIrLayer { Name = "A-WALL", EntityCount = 12, PrimitiveCount = 12 };
            JObject o = layer.ToJson();
            Assert.Equal(12, o.Value<int>("primitive_count"));
            Assert.Null(o["counts_differ_means"]);
        }

        [Fact]
        public void The_fingerprint_changes_when_the_reading_means_something_different()
        {
            CadIr a = Sample(), b = Sample();
            Assert.Equal(a.Fingerprint(), b.Fingerprint());

            b.Entities[0].Layer = "P-DOMW";
            Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
        }

        [Fact]
        public void Two_different_readings_never_fingerprint_the_same()
        {
            // THE QUIETEST WRONG ANSWER IN THE WHOLE CONVERSION. The canonical form
            // joined an entity's fields with a vertical bar, and a vertical bar
            // occurs in layer names, in paths and in a drawing's text. A layer
            // called "A|B" with no text and a layer called "A" whose text is "B"
            // produced the same string - so the same fingerprint, which an
            // incremental run reads as "the drawing has not changed".
            CadIr a = Sample(), b = Sample();
            a.Entities[0].Layer = "A|B";
            a.Entities[0].Text = null;
            b.Entities[0].Layer = "A";
            b.Entities[0].Text = "B";

            Assert.NotEqual(a.Canonical(), b.Canonical());
            Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
        }

        [Fact]
        public void A_layer_name_carrying_the_field_separator_still_fingerprints_apart()
        {
            CadIr a = Sample(), b = Sample();
            a.Layers[0].Name = "P-SANI:7";
            b.Layers[0].Name = "P-SANI";
            b.Layers[0].EntityCount = 7;
            Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
        }

        [Fact]
        public void The_fingerprint_ignores_when_the_reading_happened()
        {
            CadIr a = Sample(), b = Sample();
            b.ReadUtc = DateTime.UtcNow.AddHours(3).ToString("o");
            Assert.Equal(a.Fingerprint(), b.Fingerprint());
        }

        [Fact]
        public void A_reading_by_a_different_reader_is_a_different_reading()
        {
            CadIr a = Sample(), b = Sample();
            b.Reader = new CadReaderCapability("other-reader", "1.0", "")
                .Declare(CadAxes.Geometry, CadAxisState.Supplied, "all of it");
            Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
        }

        [Fact]
        public void An_arc_entity_carries_its_chords_and_its_curve()
        {
            // THE DEFECT THIS PINS. An arc used to be stored as its three defining
            // points - start, middle, end - and ToSegments turned those into TWO
            // chords. A quarter-circle chorded to a declared 5 mm became one
            // chorded to whatever two straight lines give, and every node, junction
            // and length downstream was computed from a shape nobody drew.
            var ir = new CadIr { Reader = Thin() };
            var arc = new CadArcFact("curve-1", new CadPoint(0, 0), 300,
                                     new CadPoint(300, 0), new CadPoint(0, 300),
                                     new CadPoint(212.132, 212.132), "P-SANI", 8, 5.0);

            var entity = new CadIrEntity { Id = "e1", Kind = CadEntityKind.Arc, Layer = "P-SANI", Arc = arc };
            // Eight chords, as the harvest produced them.
            for (int i = 0; i <= 8; i++)
            {
                double a = i * (Math.PI / 2) / 8;
                entity.Points.Add(new CadPoint(300 * Math.Cos(a), 300 * Math.Sin(a)));
            }
            ir.Entities.Add(entity);

            Assert.Equal(8, ir.ToSegments().Count);
            Assert.All(ir.ToSegments(), s2 => Assert.Equal(CadCurveKind.Arc, s2.SourceKind));

            // And the real curve is still there for a rule that can build one.
            CadArcFact kept = Assert.Single(ir.ToArcs());
            Assert.Equal(300, kept.RadiusMm, 6);
        }

        [Fact]
        public void A_polyline_entity_becomes_one_segment_per_span_for_the_topology_rules()
        {
            // The topology rules only speak line work, so the IR hands them
            // segments - and a three-point polyline is two of them, not one.
            var ir = Sample();
            Assert.Equal(2, ir.ToSegments().Count(s => s.Layer == "P-SANI"));
        }

        private static CadIr Sample()
        {
            var ir = new CadIr
            {
                SourceName = "DEMO-UNITS.dwg",
                SourceSha256 = "abc123",
                Reader = new CadReaderCapability("test-reader", "1.0", "")
                    .Declare(CadAxes.Geometry, CadAxisState.Supplied, "all of it")
            };
            ir.Entities.Add(new CadIrEntity
            {
                Id = "e1",
                Kind = CadEntityKind.Polyline,
                Layer = "P-SANI",
                Points = { new CadPoint(0, 0), new CadPoint(1000, 0), new CadPoint(2000, 0) }
            });
            ir.Layers.Add(new CadIrLayer { Name = "P-SANI", EntityCount = 1 });
            return ir;
        }
    }

    public class CadUnitRulesTests
    {
        private const double Tol = 2.0;

        private static List<CadSegment> Box(double x, double y, double w, double h, string layer = "A-WALL")
        {
            var a = new CadPoint(x, y);
            var b = new CadPoint(x + w, y);
            var c = new CadPoint(x + w, y + h);
            var d = new CadPoint(x, y + h);
            return new List<CadSegment>
            {
                new CadSegment(a, b, layer), new CadSegment(b, c, layer),
                new CadSegment(c, d, layer), new CadSegment(d, a, layer)
            };
        }

        private static CadUnitRegion Region(string id, IEnumerable<CadSegment> contents, string level = null) =>
            new CadUnitRegion { Id = id, Level = level, Contents = contents.ToList() };

        [Fact]
        public void Two_identical_layouts_side_by_side_are_one_type_with_two_occurrences()
        {
            var one = Box(0, 0, 4000, 3000);
            var two = Box(10000, 0, 4000, 3000);

            CadUnitReading reading = CadUnitRules.Recognise(
                new List<CadUnitRegion> { Region("A", one), Region("B", two) }, Tol);

            Assert.Single(reading.Types);
            Assert.Equal(2, reading.Occurrences.Count);
            Assert.All(reading.Occurrences, o => Assert.True(o.Exact));
            Assert.Empty(reading.NearMisses);
            Assert.Empty(reading.UnrecognisedRegions);
        }

        [Fact]
        public void A_type_recognised_here_is_never_given_a_name()
        {
            // The name lives in the drawing's text. Calling it A1 because it was
            // found first is the kind of plausible wrong answer nobody checks.
            CadUnitReading reading = CadUnitRules.Recognise(
                new List<CadUnitRegion>
                {
                    Region("A", Box(0, 0, 4000, 3000)),
                    Region("B", Box(9000, 0, 4000, 3000))
                }, Tol);

            CadUnitType type = Assert.Single(reading.Types);
            Assert.Null(type.Name);
            Assert.False(type.ToJson().Value<bool>("named"));
            Assert.Contains("anonymous in this reading", type.ToJson().Value<string>("name_means"));
        }

        [Fact]
        public void A_mirrored_unit_across_a_corridor_is_the_same_type_and_the_transform_says_mirrored()
        {
            // An L drawn one way, and the same L mirrored: the classic pair either
            // side of a corridor. The shape is not symmetric, so only a mirror fits.
            var original = LShape(0, 0, false);
            var mirrored = LShape(20000, 0, true);

            CadUnitReading reading = CadUnitRules.Recognise(
                new List<CadUnitRegion> { Region("A", original), Region("B", mirrored) }, Tol);

            Assert.Single(reading.Types);
            Assert.Equal(2, reading.Occurrences.Count);
            Assert.All(reading.Occurrences, o => Assert.True(o.Exact));
            Assert.Contains(reading.Occurrences, o => o.Transform.MirrorX);
        }

        [Fact]
        public void Two_regions_that_differ_by_one_wall_stay_two_regions()
        {
            // THE ONE THAT MATTERS. A 96% similarity score here puts one unit's
            // outlets in another's walls, on every floor, identically.
            var plain = Box(0, 0, 4000, 3000);
            var withPartition = Box(10000, 0, 4000, 3000);
            withPartition.Add(new CadSegment(new CadPoint(12000, 0), new CadPoint(12000, 3000), "A-WALL"));

            CadUnitReading reading = CadUnitRules.Recognise(
                new List<CadUnitRegion> { Region("A", plain), Region("B", withPartition) }, Tol);

            // Different segment counts, so the signatures differ and neither is a
            // type: two singletons that fit nothing.
            Assert.Empty(reading.Types);
            Assert.Equal(2, reading.UnrecognisedRegions.Count);
            Assert.Empty(reading.Occurrences);
        }

        [Fact]
        public void A_symmetric_layout_says_its_orientation_is_one_answer_among_several()
        {
            // A plain rectangle fits under all eight rigid maps. Returning the
            // first and calling it the unit's orientation is a reported
            // measurement that was decided by loop order - the geometry is the
            // same either way, and what a mirrored instance should be CALLED is not.
            CadUnitReading reading = CadUnitRules.Recognise(
                new List<CadUnitRegion>
                {
                    Region("A", Box(0, 0, 4000, 3000)),
                    Region("B", Box(10000, 0, 4000, 3000))
                }, Tol);

            CadUnitOccurrence fitted = reading.Occurrences.Single(o => o.RegionId == "B");
            Assert.True(fitted.Exact);
            Assert.True(fitted.ExactFitCount > 1);
            Assert.True(fitted.OrientationAmbiguous);
            Assert.Contains("SYMMETRIC", fitted.ToJson().Value<string>("orientation_means"));
            Assert.Equal(1, reading.ToJson()["summary"].Value<int>("ambiguous_orientation"));
        }

        [Fact]
        public void A_chiral_layout_has_exactly_one_orientation_and_says_so()
        {
            // An L is not symmetric, so exactly one map reproduces it and the
            // transform reported IS the unit's orientation.
            CadUnitReading reading = CadUnitRules.Recognise(
                new List<CadUnitRegion>
                {
                    Region("A", LShape(0, 0, false)),
                    Region("B", LShape(20000, 0, true))
                }, Tol);

            CadUnitOccurrence fitted = reading.Occurrences.Single(o => o.RegionId == "B");
            Assert.True(fitted.Exact);
            Assert.Equal(1, fitted.ExactFitCount);
            Assert.False(fitted.OrientationAmbiguous);
            Assert.True(fitted.Transform.MirrorX);
        }

        [Fact]
        public void No_occurrence_is_ever_a_partial_fit()
        {
            // THE INVARIANT THE FIX ESTABLISHED. A region in a shared signature
            // bucket whose best transform explained only part of the type used to
            // be recorded as an occurrence with exact=false, while the same pair
            // reached through the leftover path was recorded as a near miss - two
            // paths through one question, disagreeing over a performance detail.
            //
            // This asserts the invariant rather than the path: nothing this
            // recogniser calls an occurrence is ever a partial fit. Constructing a
            // region that shares a signature and fits no transform takes a
            // deliberately contrived shape, and the invariant is what matters.
            CadUnitReading reading = CadUnitRules.Recognise(
                new List<CadUnitRegion>
                {
                    Region("A", Box(0, 0, 4000, 3000)),
                    Region("B", Box(10000, 0, 4000, 3000)),
                    Region("C", Rotated(Box(20000, 0, 4000, 3000)))
                }, Tol);

            Assert.All(reading.Occurrences, o => Assert.True(o.Exact));
            Assert.Equal(0, reading.ToJson()["summary"].Value<int>("partial_occurrences"));
        }

        /// <summary>The same box with one corner pulled out of place, so it fits nothing exactly.</summary>
        private static List<CadSegment> Rotated(List<CadSegment> box)
        {
            var moved = new List<CadSegment>(box);
            CadSegment last = moved[moved.Count - 1];
            moved[moved.Count - 1] = new CadSegment(
                new CadPoint(last.A.X + 700, last.A.Y), last.B, last.Layer);
            return moved;
        }

        [Fact]
        public void Two_regions_that_enclose_nothing_do_not_become_a_unit_type()
        {
            // THE MOST CONFIDENT WRONG ANSWER THIS FILE CAN PRODUCE. Two empty
            // boundaries have identical signatures - zero segments, zero length, a
            // zero-by-zero box - so they match each other EXACTLY, and the reading
            // reports one unit type with two occurrences and no geometry. A
            // boundary in the wrong place looks exactly like one whose content
            // this reader drops, so they are excluded and NAMED.
            CadUnitReading reading = CadUnitRules.Recognise(
                new List<CadUnitRegion>
                {
                    Region("A", new List<CadSegment>()),
                    Region("B", new List<CadSegment>())
                }, Tol);

            Assert.Empty(reading.Types);
            Assert.Empty(reading.Occurrences);
            Assert.Equal(new[] { "A", "B" }, reading.EmptyRegions.ToArray());
            Assert.Contains("no geometry", reading.ToJson().Value<string>("empty_regions_mean"));
        }

        [Fact]
        public void An_empty_region_beside_real_ones_is_excluded_and_the_rest_still_read()
        {
            CadUnitReading reading = CadUnitRules.Recognise(
                new List<CadUnitRegion>
                {
                    Region("A", Box(0, 0, 4000, 3000)),
                    Region("B", Box(10000, 0, 4000, 3000)),
                    Region("EMPTY", new List<CadSegment>())
                }, Tol);

            Assert.Single(reading.Types);
            Assert.Equal(2, reading.Occurrences.Count);
            Assert.Equal(new[] { "EMPTY" }, reading.EmptyRegions.ToArray());
            Assert.DoesNotContain("EMPTY", reading.UnrecognisedRegions);
        }

        [Fact]
        public void A_region_that_matches_nothing_is_unrecognised_rather_than_a_type_of_one()
        {
            CadUnitReading reading = CadUnitRules.Recognise(
                new List<CadUnitRegion> { Region("A", Box(0, 0, 4000, 3000)) }, Tol);

            Assert.Empty(reading.Types);
            Assert.Equal(new[] { "A" }, reading.UnrecognisedRegions.ToArray());
        }

        [Fact]
        public void Singletons_become_types_only_when_the_caller_asks()
        {
            CadUnitReading reading = CadUnitRules.Recognise(
                new List<CadUnitRegion> { Region("A", Box(0, 0, 4000, 3000)) }, Tol, true);

            Assert.Single(reading.Types);
            Assert.Single(reading.Occurrences);
            Assert.Empty(reading.UnrecognisedRegions);
        }

        [Fact]
        public void A_unit_offset_by_a_hundredth_of_a_millimetre_still_matches()
        {
            // THE DEFECT THIS PINS. Matching used to hash both ends to a quantized
            // string and require the strings to be equal, so two points either
            // side of a bucket edge failed to match for a reason that has nothing
            // to do with the building. The grid is now only a way of finding
            // candidates; the distance decides.
            var one = Box(0, 0, 4000, 3000);
            var two = Box(10000.01, 0.01, 4000, 3000);

            CadUnitReading reading = CadUnitRules.Recognise(
                new List<CadUnitRegion> { Region("A", one), Region("B", two) }, Tol);

            Assert.Single(reading.Types);
            Assert.Equal(2, reading.Occurrences.Count);
            Assert.All(reading.Occurrences, o => Assert.True(o.Exact));
        }

        [Fact]
        public void Stacking_repeats_a_placement_on_the_levels_it_is_given_and_chooses_none()
        {
            CadUnitReading reading = CadUnitRules.Recognise(
                new List<CadUnitRegion>
                {
                    Region("A", Box(0, 0, 4000, 3000), "L03"),
                    Region("B", Box(10000, 0, 4000, 3000), "L03")
                }, Tol);

            CadUnitOccurrence first = reading.Occurrences[0];
            List<CadUnitOccurrence> stacked = CadUnitRules.Stack(
                first, new[] { "L04", "L05", "L06" },
                new Dictionary<string, double> { { "L04", 3000 }, { "L05", 6000 }, { "L06", 9000 } });

            Assert.Equal(3, stacked.Count);
            Assert.Equal(new[] { "L04", "L05", "L06" }, stacked.Select(s => s.Level).ToArray());
            Assert.Equal(new[] { 3000.0, 6000.0, 9000.0 }, stacked.Select(s => s.Transform.OffsetZ).ToArray());

            // The plan position is untouched: stacking moves a unit UP, not along.
            Assert.All(stacked, s => Assert.Equal(first.Transform.OffsetX, s.Transform.OffsetX));
            Assert.All(stacked, s => Assert.Equal(first.Transform.QuarterTurns, s.Transform.QuarterTurns));
        }

        [Fact]
        public void Instantiating_an_occurrence_reproduces_the_region_it_was_fitted_to()
        {
            // The arithmetic half must be exact, because everything else trusts it.
            var one = Box(0, 0, 4000, 3000);
            var two = Box(10000, 7000, 4000, 3000);

            CadUnitReading reading = CadUnitRules.Recognise(
                new List<CadUnitRegion> { Region("A", one), Region("B", two) }, Tol);

            CadUnitType type = reading.Types[0];
            CadUnitOccurrence second = reading.Occurrences.Single(o => o.RegionId == "B");
            List<CadSegment> built = CadUnitRules.Instantiate(type, second);

            Assert.Equal(two.Count, built.Count);
            foreach (CadSegment drawn in two)
                Assert.Contains(built, b =>
                    (b.A.PlanDistanceTo(drawn.A) <= Tol && b.B.PlanDistanceTo(drawn.B) <= Tol) ||
                    (b.A.PlanDistanceTo(drawn.B) <= Tol && b.B.PlanDistanceTo(drawn.A) <= Tol));
        }

        [Fact]
        public void Only_segments_wholly_inside_a_boundary_belong_to_it()
        {
            // A party wall crossing the boundary belongs to neither unit, or two
            // adjacent apartments differ by whichever claimed it.
            var ring = new List<CadPoint>
            {
                new CadPoint(0, 0), new CadPoint(4000, 0),
                new CadPoint(4000, 3000), new CadPoint(0, 3000)
            };
            var segments = new List<CadSegment>
            {
                new CadSegment(new CadPoint(1000, 1000), new CadPoint(2000, 1000), "A-WALL"),
                new CadSegment(new CadPoint(3500, 1500), new CadPoint(5000, 1500), "A-WALL")
            };

            List<CadSegment> inside = CadUnitRules.Inside(ring, segments);
            CadSegment kept = Assert.Single(inside);
            Assert.Equal(1000, kept.A.X, 3);
        }

        [Fact]
        public void A_transform_applies_mirror_then_rotation_then_offset()
        {
            var t = new CadUnitTransform(1, true, 100, 200, 0);
            CadPoint p = t.Apply(new CadPoint(10, 20, 5));
            // mirror: (-10, 20); quarter turn: (-20, -10); offset: (80, 190)
            Assert.Equal(80, p.X, 6);
            Assert.Equal(190, p.Y, 6);
            Assert.Equal(5, p.Z, 6);
        }

        [Fact]
        public void A_transform_survives_a_round_trip_through_json()
        {
            var original = new CadUnitTransform(3, true, 1234.5, -678.25, 3000);
            CadUnitTransform back = CadUnitTransform.FromJson(original.ToJson());
            Assert.Equal(original.QuarterTurns, back.QuarterTurns);
            Assert.Equal(original.MirrorX, back.MirrorX);
            Assert.Equal(original.OffsetX, back.OffsetX, 4);
            Assert.Equal(original.OffsetY, back.OffsetY, 4);
            Assert.Equal(original.OffsetZ, back.OffsetZ, 4);
        }

        // ---- stacking: a declared zero and an undeclared one ------------------

        private static CadUnitOccurrence Somewhere() => new CadUnitOccurrence
        {
            UnitTypeId = "u1",
            RegionId = "r1",
            Transform = new CadUnitTransform(0, false, 12000, 8000, 0),
            MatchedSegments = 6,
            TypeSegments = 6
        };

        [Fact]
        public void A_stacked_level_with_a_declared_elevation_carries_it_and_says_so()
        {
            var elevations = new Dictionary<string, double> { ["L02"] = 3200, ["L03"] = 6400 };
            List<CadUnitOccurrence> stack = CadUnitRules.Stack(
                Somewhere(), new[] { "L02", "L03" }, elevations);

            Assert.Equal(2, stack.Count);
            Assert.All(stack, o => Assert.True(o.ElevationDeclared));
            Assert.Equal(3200, stack[0].Transform.OffsetZ, 4);
            Assert.Equal(6400, stack[1].Transform.OffsetZ, 4);

            // The plan position is the same on every floor; only the height moves.
            Assert.All(stack, o => Assert.Equal(12000, o.Transform.OffsetX, 4));
        }

        [Fact]
        public void A_level_nobody_gave_a_height_to_is_not_a_ground_floor()
        {
            // One level in the map, one not - a spelling that does not match is the
            // commonest way this happens, and it is silent.
            var elevations = new Dictionary<string, double> { ["L02"] = 3200 };
            List<CadUnitOccurrence> stack = CadUnitRules.Stack(
                Somewhere(), new[] { "L02", "Level 3" }, elevations);

            Assert.True(stack[0].ElevationDeclared);
            Assert.False(stack[1].ElevationDeclared);

            // Both would be zero-or-a-number in the transform; the reply is what
            // distinguishes a ground floor from nobody having said.
            Assert.Equal(0, stack[1].Transform.OffsetZ, 4);
            Assert.Contains("NOBODY DECLARED A HEIGHT",
                            stack[1].ToJson().Value<string>("elevation_means"));
            Assert.DoesNotContain("NOBODY DECLARED A HEIGHT",
                                  stack[0].ToJson().Value<string>("elevation_means"));
        }

        [Fact]
        public void Stacking_without_any_elevations_declares_none_of_them()
        {
            List<CadUnitOccurrence> stack = CadUnitRules.Stack(
                Somewhere(), new[] { "L02", "L03", "L04" });

            // Three copies in one place. In plan that is indistinguishable from a
            // correct stack, which is exactly why it is reported.
            Assert.Equal(3, stack.Count);
            Assert.All(stack, o => Assert.False(o.ElevationDeclared));
            Assert.All(stack, o => Assert.Equal(0, o.Transform.OffsetZ, 4));
        }

        /// <summary>An L, so that a mirror is distinguishable from a rotation.</summary>
        private static List<CadSegment> LShape(double x, double y, bool mirrored)
        {
            double s = mirrored ? -1 : 1;
            CadPoint P(double dx, double dy) => new CadPoint(x + s * dx, y + dy);
            var points = new[]
            {
                P(0, 0), P(4000, 0), P(4000, 1500), P(1500, 1500), P(1500, 3000), P(0, 3000)
            };
            var segments = new List<CadSegment>();
            for (int i = 0; i < points.Length; i++)
                segments.Add(new CadSegment(points[i], points[(i + 1) % points.Length], "A-WALL"));
            return segments;
        }
    }
}
