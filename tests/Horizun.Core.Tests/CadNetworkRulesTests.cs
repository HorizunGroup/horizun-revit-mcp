// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// WHAT THE NETWORK READING MUST NEVER DO.
//
// Every case here is written against a way of being wrong that produces a model
// somebody would accept. That is the whole selection criterion: a network reading
// that drops a junction is noticed the first time somebody runs a system browser,
// and a network reading that INVENTS one is noticed after the drywall.
//
//   A CROSSING IS NOT A TEE. Two lines crossing in a plan with no shared endpoint
//   are two services at different heights. Joining them gives a model where waste
//   routes through the water main - and it flows, sizes, schedules and clashes
//   perfectly cleanly.
//
//   A GAP IS NOT CLOSED SILENTLY. Ends beyond the declared tolerance stay apart
//   and are REPORTED. The alternative - a tolerance quietly widened until things
//   meet - joins the pair being looked at and every other pair that close.
//
//   A DECLARED ELEVATION CHANGE IS NOT AN ELBOW. Two runs meeting in plan at
//   different declared heights are a riser. An elbow there is a bent pipe through
//   a slab.
//
//   FIVE RUNS AT A POINT IS NOT A CROSS. No fitting takes five, and building a
//   cross from four of them leaves an orphan nobody is told about.
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
    public class CadNetworkRulesTests
    {
        private static CadSegment Seg(double x1, double y1, double x2, double y2,
                                      string layer = "P-SANI", string curve = null) =>
            new CadSegment(new CadPoint(x1, y1, 0), new CadPoint(x2, y2, 0), layer,
                           CadCurveKind.Line, 0, curve ?? (x1 + "," + y1 + "-" + x2 + "," + y2));

        private static CadNetworkOptions Options() => new CadNetworkOptions
        {
            ConnectToleranceMm = 1.0,
            GapReviewDistanceMm = 50.0,
            CollinearToleranceDegrees = 2.0,
            ThroughToleranceDegrees = 15.0
        };

        // ------------------------------------------------------------------ runs

        [Fact]
        public void A_polyline_drawn_as_many_collinear_pieces_is_one_run()
        {
            // A draughtsman's straight main, drawn in four goes. Four pipes where
            // the drawing shows one is four elements, three joints that do not
            // exist, and a schedule nobody recognises.
            var segments = new List<CadSegment>
            {
                Seg(0, 0, 1000, 0), Seg(1000, 0, 2000, 0),
                Seg(2000, 0, 3000, 0), Seg(3000, 0, 4000, 0)
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options());

            Assert.Single(net.Runs);
            Assert.Equal(4000, net.Runs[0].LengthMm, 3);
            Assert.Equal(4, net.Runs[0].MergedSegments);
        }

        [Fact]
        public void The_reading_reports_how_much_of_the_drawing_was_drafting()
        {
            // A straight main drawn in four pieces is ONE pipe, and three of those
            // pieces are an artefact of drafting rather than a fact about the
            // building. The number was being computed and thrown away.
            var segments = new List<CadSegment>
            {
                Seg(0, 0, 1000, 0), Seg(1000, 0, 2000, 0),
                Seg(2000, 0, 3000, 0), Seg(3000, 0, 4000, 0)
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options());

            JObject summary = net.SummaryJson();
            Assert.Equal(4, summary.Value<int>("segments_given"));
            Assert.Equal(3, summary.Value<int>("segments_merged_away"));
            Assert.Contains("artefact of drafting", summary.Value<string>("merge_means"));
        }

        [Fact]
        public void A_long_run_names_every_entity_it_was_built_from()
        {
            // THE DEFECT THIS PINS. Source attribution used to key the original
            // segments by their two ends and look the merged run up by ITS two
            // ends - which belong to no single segment. The lookup missed on
            // exactly the runs merging had helped, so provenance came back empty
            // for the long mains and looked fine for the stubs.
            var segments = new List<CadSegment>
            {
                Seg(0, 0, 1000, 0, "P-SANI", "curve-a"),
                Seg(1000, 0, 2000, 0, "P-SANI", "curve-b"),
                Seg(2000, 0, 3000, 0, "P-SANI", "curve-c")
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options());

            Assert.Single(net.Runs);
            Assert.Equal(new[] { "curve-a", "curve-b", "curve-c" },
                         net.Runs[0].SourceEntityIds.ToArray());
        }

        // ------------------------------------------------------------- junctions

        [Fact]
        public void A_corner_is_an_elbow_at_the_drawings_own_angle()
        {
            var segments = new List<CadSegment> { Seg(0, 0, 1000, 0), Seg(1000, 0, 1000, 800) };
            CadNetwork net = CadNetworkRules.Build(segments, Options());

            CadJunction corner = net.Junctions.Single(j => j.Degree == 2);
            Assert.Equal(CadJunctionKind.Elbow, corner.Kind);
            Assert.True(corner.Automatic);
            Assert.Contains("90", corner.Says);

            CadConnectionIntent intent = net.Connections.Single(c => c.JunctionNode == corner.NodeKey);
            Assert.Equal("elbow", intent.Fitting);
            Assert.True(intent.Automatic);
        }

        [Fact]
        public void Three_runs_with_a_through_pair_is_a_tee_and_the_branch_is_named()
        {
            var segments = new List<CadSegment>
            {
                Seg(0, 0, 1000, 0), Seg(1000, 0, 2000, 0), Seg(1000, 0, 1000, 900)
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options());

            CadJunction tee = net.Junctions.Single(j => j.Degree == 3);
            Assert.Equal(CadJunctionKind.Tee, tee.Kind);
            Assert.Equal(2, tee.ThroughRunIds.Count);
            Assert.NotNull(tee.BranchRunId);
            Assert.DoesNotContain(tee.BranchRunId, tee.ThroughRunIds);

            // The branch is the one going north, not whichever was enumerated third.
            CadRun branch = net.Runs.Single(r => r.Id == tee.BranchRunId);
            Assert.Equal(900, branch.LengthMm, 3);
        }

        [Fact]
        public void Three_runs_with_no_through_pair_is_irregular_and_builds_nothing()
        {
            // A wye at 120 degrees. Which leg is the branch decides which way the
            // fitting faces, and the geometry does not say.
            var segments = new List<CadSegment>
            {
                Seg(1000, 1000, 1000, 2000),
                Seg(1000, 1000, 134, 500),
                Seg(1000, 1000, 1866, 500)
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options());

            CadJunction j = net.Junctions.Single(x => x.Degree == 3);
            Assert.Equal(CadJunctionKind.Irregular, j.Kind);
            Assert.False(j.Automatic);
            Assert.False(net.Connections.Single(c => c.JunctionNode == j.NodeKey).Automatic);
        }

        [Fact]
        public void Four_runs_in_two_straight_pairs_is_a_cross()
        {
            var segments = new List<CadSegment>
            {
                Seg(0, 1000, 1000, 1000), Seg(1000, 1000, 2000, 1000),
                Seg(1000, 0, 1000, 1000), Seg(1000, 1000, 1000, 2000)
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options());

            CadJunction cross = net.Junctions.Single(j => j.Degree == 4);
            Assert.Equal(CadJunctionKind.Cross, cross.Kind);
            Assert.True(cross.Automatic);
            Assert.Equal(4, cross.ThroughRunIds.Count);
        }

        [Fact]
        public void Five_runs_at_one_point_is_refused_not_rounded_down_to_a_cross()
        {
            var segments = new List<CadSegment>
            {
                Seg(1000, 1000, 0, 1000), Seg(1000, 1000, 2000, 1000),
                Seg(1000, 1000, 1000, 0), Seg(1000, 1000, 1000, 2000),
                Seg(1000, 1000, 1700, 1700)
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options());

            CadJunction j = net.Junctions.Single(x => x.Degree == 5);
            Assert.Equal(CadJunctionKind.Unsupported, j.Kind);
            Assert.False(j.Automatic);
            Assert.Equal("none", net.Connections.Single(c => c.JunctionNode == j.NodeKey).Fitting);
        }

        // ------------------------------------------------------- what is refused

        [Fact]
        public void A_crossing_with_no_shared_end_is_reported_and_never_joined()
        {
            // THE ONE THAT MATTERS. Waste crossing a water main in plan.
            var segments = new List<CadSegment>
            {
                Seg(0, 1000, 2000, 1000, "P-SANI"),
                Seg(1000, 0, 1000, 2000, "P-DOMW")
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options());

            Assert.Equal(2, net.Runs.Count);
            Assert.Single(net.Crossings);
            Assert.False(net.Crossings[0].SameLayer);

            // Not one junction of degree above one: the two runs never meet.
            Assert.All(net.Junctions, j => Assert.Equal(1, j.Degree));
            Assert.Empty(net.Connections);

            // And they are two separate networks, which is the truth.
            Assert.Equal(2, net.Components.Count);
        }

        [Fact]
        public void A_gap_wider_than_the_tolerance_is_reported_and_not_bridged()
        {
            var segments = new List<CadSegment> { Seg(0, 0, 1000, 0), Seg(1012, 0, 2000, 0) };
            CadNetwork net = CadNetworkRules.Build(segments, Options());

            Assert.Equal(2, net.Runs.Count);
            Assert.Equal(2, net.Components.Count);

            CadGap gap = Assert.Single(net.Gaps);
            Assert.Equal(12, gap.DistanceMm, 3);
            Assert.Empty(net.Connections);
        }

        [Fact]
        public void A_gap_inside_the_tolerance_is_one_node_and_is_not_reported_as_a_gap()
        {
            var segments = new List<CadSegment> { Seg(0, 0, 1000, 0), Seg(1000.4, 0, 2000, 0) };
            CadNetwork net = CadNetworkRules.Build(segments, Options());

            Assert.Empty(net.Gaps);
            Assert.Single(net.Components);
            Assert.Single(net.Runs);
        }

        [Fact]
        public void Runs_declared_at_different_elevations_do_not_meet_in_the_building()
        {
            // A drop drawn as a corner. In plan it is an elbow; at +2400 and +400
            // it is a riser, and an elbow there is a bent pipe through a slab.
            var segments = new List<CadSegment>
            {
                Seg(0, 0, 1000, 0, "P-HIGH"),
                Seg(1000, 0, 1000, 800, "P-LOW")
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options(), layer =>
                layer == "P-HIGH"
                    ? new CadNetworkRules.CadRunDeclaration { ElevationMm = 2400, SystemType = "Sanitary" }
                    : new CadNetworkRules.CadRunDeclaration { ElevationMm = 400, SystemType = "Sanitary" });

            CadJunction j = net.Junctions.Single(x => x.Degree == 2);
            Assert.Equal(CadJunctionKind.ElevationChange, j.Kind);
            Assert.False(j.Automatic);
            Assert.Contains("2400", j.Says);
            Assert.Contains("400", j.Says);
            Assert.Equal("none", net.Connections.Single(c => c.JunctionNode == j.NodeKey).Fitting);
        }

        // ------------------------------------------------------------ components

        [Fact]
        public void Two_systems_in_one_connected_network_is_a_conflict_and_is_not_resolved()
        {
            var segments = new List<CadSegment>
            {
                Seg(0, 0, 1000, 0, "P-SANI"),
                Seg(1000, 0, 1000, 900, "P-DOMW")
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options(), layer =>
                new CadNetworkRules.CadRunDeclaration
                {
                    SystemType = layer == "P-SANI" ? "Sanitary" : "Domestic Cold Water"
                });

            CadNetworkComponent component = Assert.Single(net.Components);
            Assert.Equal(2, component.DeclaredSystems.Count);

            // Asserted on the FLAG a caller branches on, not on a word in the prose:
            // the first version looked for "conflict" in `means`, which the message
            // never says - it says the same thing in longer words. A test that pins
            // wording fails when the wording improves and passes when the behaviour
            // breaks.
            JObject json = component.ToJson();
            Assert.True(json.Value<bool>("system_conflict"));
            Assert.Contains("NOT resolved", json.Value<string>("means"));
            Assert.Contains("Sanitary", json["declared_systems"].ToString());
            Assert.Contains("Domestic Cold Water", json["declared_systems"].ToString());
        }

        [Fact]
        public void A_run_with_no_declaration_carries_null_not_zero()
        {
            // Null and zero are the difference between "nobody said how high this
            // is" and "this is on the slab".
            CadNetwork net = CadNetworkRules.Build(new List<CadSegment> { Seg(0, 0, 1000, 0) }, Options());
            Assert.Null(net.Runs[0].ElevationMm);
            Assert.Null(net.Runs[0].DiameterMm);
            Assert.Null(net.Runs[0].SystemType);
        }

        [Fact]
        public void An_open_end_is_a_terminal_and_nothing_is_placed_at_it()
        {
            CadNetwork net = CadNetworkRules.Build(new List<CadSegment> { Seg(0, 0, 1000, 0) }, Options());

            Assert.Equal(2, net.Terminals.Count());
            Assert.All(net.Terminals, t => Assert.False(t.Automatic));
            Assert.Empty(net.Connections);
        }

        [Fact]
        public void A_split_straight_length_is_joined_directly_with_no_fitting()
        {
            var segments = new List<CadSegment> { Seg(0, 0, 1000, 0), Seg(1000, 0, 2500, 0) };
            // Collinear pieces merge into one run, so force a real degree-two node
            // by putting them on different layers: two services in line.
            segments[1] = new CadSegment(new CadPoint(1000, 0, 0), new CadPoint(2500, 0, 0),
                                         "P-SANI-2", CadCurveKind.Line, 0, "curve-2");
            CadNetwork net = CadNetworkRules.Build(segments, Options());

            CadJunction j = net.Junctions.Single(x => x.Degree == 2);
            Assert.Equal(CadJunctionKind.Collinear, j.Kind);
            Assert.True(j.Automatic);
            Assert.Equal("direct", net.Connections.Single(c => c.JunctionNode == j.NodeKey).Fitting);
        }

        [Fact]
        public void The_crossing_check_says_when_it_was_skipped_rather_than_reporting_none()
        {
            // A reading that skipped the check and reported zero crossings would be
            // read as "nothing crosses", which is the opposite of what happened.
            var segments = new List<CadSegment>();
            for (int i = 0; i < 12; i++) segments.Add(Seg(0, i * 100, 1000, i * 100, "L" + i));

            CadNetworkOptions options = Options();
            options.CrossingCheckLimit = 5;
            CadNetwork net = CadNetworkRules.Build(segments, options);

            Assert.True(net.CrossingCheckSkipped);
            Assert.Empty(net.Crossings);
            Assert.True(net.SummaryJson().Value<bool>("crossing_check_skipped"));
        }

        // ------------------------------------------------------------ arithmetic

        [Fact]
        public void A_merged_piece_too_short_to_be_a_run_is_recorded_rather_than_dropped()
        {
            // THE DEFECT THIS PINS. A run shorter than the connect tolerance used
            // to be skipped silently, and the cost is not the run: the node it
            // would have reached loses a degree, so a tee reads as an elbow and
            // the reading proposes a fitting the drawing does not show.
            var segments = new List<CadSegment>
            {
                Seg(0, 0, 1000, 0),
                Seg(1000, 0, 1000.4, 0, "P-STUB"),   // shorter than the 1 mm tolerance
                Seg(1000, 0, 1000, 900, "P-BRANCH")
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options());

            Assert.Single(net.DroppedShortRuns);
            Assert.Equal("P-STUB", net.DroppedShortRuns[0].Value<string>("layer"));
            Assert.Equal(1, net.SummaryJson().Value<int>("dropped_short_runs"));
            Assert.Contains("tee into an elbow",
                            net.SummaryJson().Value<string>("dropped_short_runs_mean"));
        }

        [Fact]
        public void A_run_nothing_could_be_attributed_to_reports_zero_not_one()
        {
            // Forcing the count to at least one made an unattributed run look
            // attributed, which is a fabricated measurement.
            var segments = new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, 0), new CadPoint(1000, 0), "P-SANI",
                               CadCurveKind.Line, 0, null)   // no source curve: nothing to attribute
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options());

            Assert.Single(net.Runs);
            Assert.Empty(net.Runs[0].SourceEntityIds);
            Assert.Equal(0, net.Runs[0].MergedSegments);
        }

        [Fact]
        public void Attribution_past_its_stated_bound_is_skipped_and_says_so()
        {
            // A network reading that takes ten minutes on a permit drawing is one
            // nobody runs twice. The bound is stated and the skip is reported -
            // an empty source_entities must not read as "nothing was found".
            var segments = new List<CadSegment>();
            for (int i = 0; i < 40; i++) segments.Add(Seg(0, i * 100, 1000, i * 100, "L" + i));

            CadNetworkOptions options = Options();
            options.AttributionWorkLimit = 10;
            CadNetwork net = CadNetworkRules.Build(segments, options);

            Assert.True(net.AttributionSkipped);
            Assert.All(net.Runs, r => Assert.Empty(r.SourceEntityIds));
            Assert.All(net.Runs, r => Assert.Equal(0, r.MergedSegments));
            Assert.Contains("nothing looked", net.SummaryJson().Value<string>("source_attribution_means"));
        }

        [Fact]
        public void A_junction_where_only_some_runs_declare_a_height_says_what_it_assumed()
        {
            // One value in the set of declared elevations is not agreement when
            // the other run simply has not been declared. It is still classified -
            // refusing would hold every junction on an undeclared layer - and the
            // sentence says which part of it is a guess.
            var segments = new List<CadSegment>
            {
                Seg(0, 0, 1000, 0, "P-HIGH"),
                Seg(1000, 0, 1000, 800, "P-UNSET")
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options(), layer =>
                layer == "P-HIGH"
                    ? new CadNetworkRules.CadRunDeclaration { ElevationMm = 2400 }
                    : new CadNetworkRules.CadRunDeclaration());   // system, bore and height all null

            CadJunction j = net.Junctions.Single(x => x.Degree == 2);
            Assert.Equal(CadJunctionKind.Elbow, j.Kind);
            Assert.Contains("no declared height", j.Says);
            Assert.Contains("nobody stated", j.Says);
        }

        [Fact]
        public void A_run_that_is_a_chord_of_a_curve_says_so()
        {
            // A curve chorded to the declared sagitta turns at every chord, so this
            // reading gives a curved duct as N straight runs with N-1 elbows -
            // within tolerance geometrically, and a piece of ductwork nobody would
            // fabricate. The limitation is stated rather than left to look like a
            // design.
            var segments = new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, 0), new CadPoint(1000, 100), "M-DUCT",
                               CadCurveKind.Arc, 0, "curve-1"),
                new CadSegment(new CadPoint(1000, 100), new CadPoint(1900, 400), "M-DUCT",
                               CadCurveKind.Arc, 1, "curve-1")
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options());

            Assert.Equal(2, net.Runs.Count);
            Assert.All(net.Runs, r => Assert.True(r.IsChordOfACurve));
            Assert.Equal(2, net.SummaryJson().Value<int>("runs_that_are_chords_of_a_curve"));
            Assert.Contains("nobody would", net.SummaryJson().Value<string>("chords_mean"));
        }

        [Fact]
        public void A_curve_whose_arc_is_supplied_becomes_ONE_run_measured_along_the_arc()
        {
            // A quarter-circle of radius 300, chorded into eight pieces. Without
            // the arc that is eight runs and seven elbows; with it, one run.
            var chords = new List<CadSegment>();
            var points = new List<CadPoint>();
            for (int i = 0; i <= 8; i++)
            {
                double a = i * (Math.PI / 2) / 8;
                points.Add(new CadPoint(300 * Math.Cos(a), 300 * Math.Sin(a)));
            }
            for (int i = 0; i < 8; i++)
                chords.Add(new CadSegment(points[i], points[i + 1], "M-DUCT",
                                          CadCurveKind.Arc, i, "curve-1"));

            var arc = new CadArcFact("curve-1", new CadPoint(0, 0), 300,
                                     points[0], points[8], points[4], "M-DUCT", 8, 5.0);

            CadNetwork withArc = CadNetworkRules.Build(chords, Options(), null,
                                                       new List<CadArcFact> { arc });
            CadNetwork withoutArc = CadNetworkRules.Build(chords, Options());

            Assert.Single(withArc.Runs);
            Assert.NotNull(withArc.Runs[0].Arc);
            Assert.False(withArc.Runs[0].IsChordOfACurve);

            // The LENGTH is the arc, not the chord: a quarter-circle of radius 300
            // is 471 mm of duct and its chord is 424 mm - eleven per cent short.
            Assert.Equal(300 * Math.PI / 2, withArc.Runs[0].LengthMm, 3);

            // And without it, the same drawing is eight runs and seven junctions.
            Assert.Equal(8, withoutArc.Runs.Count);
            Assert.All(withoutArc.Runs, r => Assert.True(r.IsChordOfACurve));
        }

        [Fact]
        public void A_curve_meeting_a_straight_run_is_classified_by_its_tangent()
        {
            // The chord of a quarter-circle points 45 degrees away from where the
            // duct actually leaves. Reading the chord turns a tangential meeting
            // into an elbow.
            var points = new List<CadPoint>();
            for (int i = 0; i <= 8; i++)
            {
                double a = i * (Math.PI / 2) / 8;
                points.Add(new CadPoint(300 * Math.Cos(a), 300 * Math.Sin(a)));
            }
            var segments = new List<CadSegment>();
            for (int i = 0; i < 8; i++)
                segments.Add(new CadSegment(points[i], points[i + 1], "M-DUCT",
                                            CadCurveKind.Arc, i, "curve-1"));
            // A straight duct leaving the arc's end (0,300) tangentially: the
            // tangent there points in -X for a counter-clockwise sweep.
            segments.Add(new CadSegment(points[8], new CadPoint(-1000, 300), "M-DUCT"));

            var arc = new CadArcFact("curve-1", new CadPoint(0, 0), 300,
                                     points[0], points[8], points[4], "M-DUCT", 8, 5.0);

            CadNetwork net = CadNetworkRules.Build(segments, Options(), null,
                                                   new List<CadArcFact> { arc });

            CadJunction meeting = net.Junctions.Single(j => j.Degree == 2);
            Assert.Equal(CadJunctionKind.Collinear, meeting.Kind);
        }

        [Fact]
        public void A_run_drawn_straight_is_not_reported_as_a_chord()
        {
            CadNetwork net = CadNetworkRules.Build(
                new List<CadSegment> { Seg(0, 0, 1000, 0) }, Options());
            Assert.False(net.Runs.Single().IsChordOfACurve);
            Assert.Equal(0, net.SummaryJson().Value<int>("runs_that_are_chords_of_a_curve"));
            Assert.Contains("drawn straight", net.SummaryJson().Value<string>("chords_mean"));
        }

        [Theory]
        [InlineData(0, 180, 180)]
        [InlineData(10, 350, 20)]
        [InlineData(350, 10, 20)]
        [InlineData(90, 270, 180)]
        public void The_angle_between_two_bearings_is_the_smaller_one(double a, double b, double expected)
            => Assert.Equal(expected, CadNetworkRules.AngleBetween(a, b), 6);
    }
}
