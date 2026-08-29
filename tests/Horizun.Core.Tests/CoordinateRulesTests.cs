using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// THE FALSE POSITIVE THIS AREA EXISTS TO AVOID.
    ///
    /// A survey point at a national grid coordinate is ten kilometres from the
    /// internal origin, and that is CORRECT - it is what a survey point is for. A
    /// tool that reads it and reports "geometry 10 km from origin" has not found a
    /// problem, it has misread the model, and it will do so on every properly
    /// set-up site in the world.
    ///
    /// These tests hold the two questions apart by construction.
    /// </summary>
    public class CoordinateRulesTests
    {
        private static OutlierFact At(double mm, long id = 1)
        {
            return new OutlierFact { ElementId = id, Category = "Walls", Name = "w", DistanceMm = mm };
        }

        [Fact]
        public void A_distant_survey_point_cannot_reach_the_distance_count_at_all()
        {
            // The survey point is 10 km out, the geometry is all within 40 m.
            var facts = new CoordinateFacts
            {
                InternalOrigin = new PointFact { Name = "internal_origin", Readable = true },
                SurveyPoint = new PointFact { Name = "survey_point", Readable = true, XMm = 10000000, YMm = 4000000 },
                ProjectBasePoint = new PointFact { Name = "project_base_point", Readable = true }
            };
            facts.Outliers.AddRange(new[] { At(12000), At(38000), At(4000) });

            double? farthest;
            long beyond = CoordinateRules.CountBeyond(facts.Outliers, CoordinateRules.DefaultFarRadiusMm, out farthest);

            Assert.Equal(0, beyond);
            Assert.Equal(38000, farthest.Value, 3);
            // And the survey point is still reported, because it is a fact about the
            // model - it is simply not an input to this count.
            Assert.True(facts.SurveyPoint.DistanceFromInternalOriginMm > 10000000 - 1);
        }

        [Fact]
        public void Geometry_beyond_the_radius_is_counted_and_the_farthest_is_reported()
        {
            var elements = new[] { At(500), At(1500000, 7), At(3000000, 9) };
            double? farthest;
            long beyond = CoordinateRules.CountBeyond(elements, CoordinateRules.DefaultFarRadiusMm, out farthest);

            Assert.Equal(2, beyond);
            Assert.Equal(3000000, farthest.Value, 3);
        }

        [Fact]
        public void An_element_that_would_not_report_a_position_makes_the_count_a_lower_bound()
        {
            string note = CoordinateRules.OriginNote(beyond: 2, radiusMm: 1000000, measured: 10, unreadable: 3);
            Assert.Contains("LOWER BOUND", note);

            string clean = CoordinateRules.OriginNote(beyond: 0, radiusMm: 1000000, measured: 10, unreadable: 0);
            Assert.DoesNotContain("LOWER BOUND", clean);
            Assert.Contains("every one of 10", clean);
        }

        [Fact]
        public void With_nothing_measured_the_note_says_nothing_is_known_rather_than_clean()
        {
            string note = CoordinateRules.OriginNote(beyond: 0, radiusMm: 1000000, measured: 0, unreadable: 0);
            Assert.Contains("nothing is known", note);
            Assert.DoesNotContain("every one of", note);
        }

        [Fact]
        public void A_non_finite_distance_is_skipped_rather_than_counted_as_far()
        {
            var elements = new[] { At(double.NaN), At(double.PositiveInfinity), At(10) };
            double? farthest;
            long beyond = CoordinateRules.CountBeyond(elements, 100, out farthest);
            Assert.Equal(0, beyond);
            Assert.Equal(10, farthest.Value, 3);
        }

        [Fact]
        public void Readability_items_answer_whether_a_point_was_READ_not_whether_it_is_good()
        {
            var facts = new CoordinateFacts
            {
                InternalOrigin = new PointFact { Readable = true },
                // A base point 50 km away is unusual and is somebody's decision. It is
                // READ, so it is satisfied - this bridge grades no decision it was not
                // given a standard for.
                ProjectBasePoint = new PointFact { Readable = true, XMm = 50000000 },
                SurveyPoint = new PointFact { Readable = false, Why = "the document reports no survey point" },
                LocationReadable = true,
                ActiveLocationName = "Internal",
                TrueNorthReadable = true,
                TrueNorthDegrees = 0,
                UnitsReadable = true,
                LengthUnitName = "millimeters"
            };

            var items = CoordinateRules.ReadabilityItems(facts);

            Assert.True(items["project_base_point"].Satisfied);
            Assert.False(items["survey_point"].Satisfied.Value);
            Assert.Contains("no survey point", items["survey_point"].Detail);
            Assert.True(items["project_location"].Satisfied);
            Assert.True(items["true_north"].Satisfied);
            Assert.True(items["length_units"].Satisfied);
        }

        [Fact]
        public void A_point_that_was_never_collected_is_null_and_never_false()
        {
            // null Satisfied is what makes the gate say not_measurable. Reporting a
            // point nobody looked for as "not satisfied" would be a finding about the
            // reader.
            var items = CoordinateRules.ReadabilityItems(new CoordinateFacts());
            Assert.Null(items["internal_origin"].Satisfied);
            Assert.Null(items["true_north"].Satisfied);
            Assert.Null(items["project_location"].Satisfied);
            Assert.Null(items["length_units"].Satisfied);

            // And ASKED-AND-ABSENT is false, which is a different answer.
            var asked = CoordinateRules.ReadabilityItems(new CoordinateFacts
            {
                TrueNorthReadable = false, UnitsReadable = false, LocationReadable = false
            });
            Assert.False(asked["true_north"].Satisfied.Value);
            Assert.Contains("would not report", asked["true_north"].Detail);
        }

        [Fact]
        public void A_link_that_will_not_say_whether_it_shares_position_is_not_counted_as_not_sharing()
        {
            var links = new List<LinkPlacementFact>
            {
                new LinkPlacementFact { TransformReadable = true, SharedPositionMatchesHost = null },
                new LinkPlacementFact { TransformReadable = true, SharedPositionMatchesHost = false },
                new LinkPlacementFact { TransformReadable = true, SharedPositionMatchesHost = true },
            };
            long reflected, rotated, offset, notSharing, unreadable;
            CoordinateRules.TallyLinks(links, 1.0, out reflected, out rotated, out offset,
                                       out notSharing, out unreadable);
            Assert.Equal(1, notSharing);
        }

        [Fact]
        public void Reflection_rotation_and_offset_are_counted_separately_because_they_mean_different_things()
        {
            var links = new List<LinkPlacementFact>
            {
                new LinkPlacementFact { TransformReadable = true, HasReflection = true },
                new LinkPlacementFact { TransformReadable = true, HasRotation = true },
                new LinkPlacementFact { TransformReadable = true, OriginOffsetMm = 5000 },
                new LinkPlacementFact { TransformReadable = false, Why = "the link would not return a transform" },
            };
            long reflected, rotated, offset, notSharing, unreadable;
            CoordinateRules.TallyLinks(links, 1.0, out reflected, out rotated, out offset,
                                       out notSharing, out unreadable);
            Assert.Equal(1, reflected);
            Assert.Equal(1, rotated);
            Assert.Equal(1, offset);
            Assert.Equal(1, unreadable);
        }

        [Fact]
        public void An_offset_inside_the_tolerance_is_not_an_offset()
        {
            var links = new List<LinkPlacementFact>
            {
                new LinkPlacementFact { TransformReadable = true, OriginOffsetMm = 0.5 },
            };
            long reflected, rotated, offset, notSharing, unreadable;
            CoordinateRules.TallyLinks(links, 1.0, out reflected, out rotated, out offset,
                                       out notSharing, out unreadable);
            Assert.Equal(0, offset);
        }

        [Fact]
        public void The_distance_explanation_says_which_of_the_two_questions_it_answered()
        {
            Assert.Contains("INTERNAL ORIGIN", CoordinateRules.DistanceMeans);
            Assert.Contains("survey point far from the internal origin is normal",
                            CoordinateRules.DistanceMeans);
        }
    }
}
