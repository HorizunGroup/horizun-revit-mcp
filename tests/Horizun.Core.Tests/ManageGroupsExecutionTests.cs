using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ManageGroupsExecutionTests
    {
        [Fact]
        public void Add_members_rehearsal_rolls_back_without_a_token_when_the_commit_omits_the_new_type()
        {
            var fixture = new Fixture();
            fixture.Document.UnreportedAdditions.Add(41);

            CommandResult result = fixture.Execute();

            Assert.False(result.Success);
            Assert.Contains("also_created", result.Error);
            JObject rehearsal = Assert.IsType<JObject>(result.Detail["rehearsal"]);
            Assert.True(rehearsal.Value<bool>("committed_inside_group"));
            Assert.True(rehearsal.Value<bool>("rollback_confirmed"));
            Assert.False(rehearsal.Value<bool>("verified"));
            Assert.False(Check(rehearsal, "also_created").Value<bool>("matches"));
            Assert.Null(result.Detail.SelectToken("$..confirmation_token"));
            fixture.AssertRestored(1);
        }

        [Fact]
        public void Add_members_apply_rolls_back_before_assimilating_an_unrehearsed_deletion()
        {
            var fixture = new Fixture();
            fixture.Rehearse();
            fixture.Document.BeforeTransactionObservation = doc => Assert.True(doc.Elements.Remove(90));

            CommandResult result = fixture.Execute();

            Assert.False(result.Success);
            Assert.Equal("rolled_back", result.Detail.Value<string>("state"));
            Assert.Equal("RolledBack", result.Detail.Value<string>("transaction_group_status"));
            JObject deletions = Check(result.Detail, "deleted_elements");
            Assert.False(deletions.Value<bool>("matches"));
            Assert.Equal(new long[] { 10, 11, 30, 31 }, deletions["requested"].Values<long>());
            Assert.Equal(new long[] { 10, 11, 30, 31, 90 }, deletions["found_in_committed_model"].Values<long>());
            fixture.AssertRestored(2);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Add_members_rolls_back_an_unrelated_addition_even_when_its_identity_can_be_read(bool apply)
        {
            var fixture = new Fixture();
            if (apply) fixture.Rehearse();
            fixture.Document.BeforeTransactionObservation = doc =>
                doc.Elements.Add(900, new Group { Id = Rid.Make(900), GroupType = new GroupType { Id = Rid.Make(901) } });

            CommandResult result = fixture.Execute();

            Assert.False(result.Success);
            Assert.Equal(apply ? "rolled_back" : "refused", result.Detail.Value<string>("state"));
            var checks = apply ? result.Detail : Assert.IsType<JObject>(result.Detail["rehearsal"]);
            JObject outOfScope = Check(checks, "out_of_scope_additions");
            Assert.False(outOfScope.Value<bool>("matches"));
            Assert.Equal(new long[] { 900 }, outOfScope["found_in_committed_model"].Values<long>());
            Assert.Null(result.Detail.SelectToken("$..confirmation_token"));
            fixture.AssertRestored(apply ? 2 : 1);
        }

        [Fact]
        public void Add_members_rolls_back_when_a_new_copy_member_has_a_different_type_than_rehearsed()
        {
            var fixture = new Fixture();
            fixture.Rehearse();
            fixture.Document.BeforeTransactionObservation = doc => doc.Elements[52] = new MemberWithDifferentType { Id = Rid.Make(52) };

            CommandResult result = fixture.Execute();

            Assert.False(result.Success);
            Assert.Equal("rolled_back", result.Detail.Value<string>("state"));
            Assert.True(Check(result.Detail, "also_created").Value<bool>("matches"));
            Assert.False(Check(result.Detail, "addition_roles").Value<bool>("matches"));
            fixture.AssertRestored(2);
        }

        private sealed class MemberWithDifferentType : Element
        {
            public override ElementId GetTypeId() => Rid.Make(123);
        }

        [Fact]
        public void Add_members_apply_reports_both_instances_and_check_variation_rechecks_all_three_members()
        {
            var fixture = new Fixture();
            fixture.Rehearse();
            fixture.Document.GroupCreationIdOffset = 1000;

            CommandResult result = fixture.Execute();

            Assert.True(result.Success, result.Error);
            Assert.True(fixture.Document.Assimilated);
            JObject applied = Assert.IsType<JObject>(result.Data);
            Assert.True(applied.Value<bool>("host_verified"));
            var changed = Assert.IsType<JObject>(applied["result"]);
            var affected = Assert.IsType<JArray>(changed["affected_group_ids"]);
            Assert.Equal(new long[] { 1040, 12 }, affected.Values<long>());
            Assert.All(affected.Values<long>(), id => Assert.Equal(3, ((Group)fixture.Document.Elements[id]).GetMemberIds().Count));
            Assert.Equal(new long[] { 1041, 1050, 1051, 1052 }, changed["also_created"].Select(row => row.Value<long>("element_id")));

            CommandResult variation = fixture.Execute(new JObject { ["operation"] = "check_variation", ["group_ids"] = affected,
                ["relative_rotations_degrees"] = new JArray(0) });
            Assert.True(variation.Success, variation.Error);
            JObject observed = Assert.IsType<JObject>(variation.Data);
            Assert.True(observed.Value<bool>("coverage_complete"));
            var count = Assert.IsType<JValue>(observed["variation_count"]);
            Assert.Equal(JTokenType.Integer, count.Type);
            Assert.Equal(0, count.Value<int>());
        }

        [Theory]
        [InlineData("type")]
        [InlineData("all_instances")]
        public void Add_members_verifies_the_rotated_copy_and_check_variation_reports_no_difference(string scope)
        {
            var fixture = new Fixture(scope, quarterTurn: true, singleCenteredMember: true);
            fixture.Rehearse();

            CommandResult applied = fixture.Execute();

            Assert.True(applied.Success, applied.Error);
            JObject data = Assert.IsType<JObject>(applied.Data);
            Assert.True(data.Value<bool>("host_verified"));
            Assert.Equal(new long[] { 52 }, CopyAdditions(data)["added_member_ids"].Values<long>());
            Assert.Equal(90, CopyAdditions(data)["placement"].Value<double>("rotation_degrees"), 8);
            BoundingBoxXYZ box = fixture.Document.Elements[52].Bounds;
            Assert.Equal(new[] { 8d, 1d, 0d }, new[] { box.Min.X, box.Min.Y, box.Min.Z });
            Assert.Equal(new[] { 10d, 2d, 3d }, new[] { box.Max.X, box.Max.Y, box.Max.Z });

            CommandResult variation = fixture.Execute(new JObject { ["operation"] = "check_variation",
                ["group_ids"] = data["result"]["affected_group_ids"], ["relative_rotations_degrees"] = new JArray(90) });
            Assert.True(variation.Success, variation.Error);
            JObject report = Assert.IsType<JObject>(variation.Data);
            Assert.True(report.Value<bool>("coverage_complete"));
            Assert.Equal(0, report.Value<int>("variation_count"));
            Assert.Equal("declared", report["instances"][0].Value<string>("rotation_source"));
        }

        [Fact]
        public void Add_members_rejects_a_member_that_was_translated_instead_of_rotated()
        {
            var fixture = new Fixture(quarterTurn: true, singleCenteredMember: true);
            fixture.Document.TranslateNewGroupMember = true;

            CommandResult result = fixture.Execute();

            Assert.False(result.Success);
            JObject rehearsal = Assert.IsType<JObject>(result.Detail["rehearsal"]);
            Assert.False(Check(rehearsal, "copy_additions").Value<bool>("matches"));
            Assert.True(CopyAdditions(rehearsal)["placement"].Value<bool>("coverage_complete"));
            Assert.Null(result.Detail.SelectToken("$..confirmation_token"));
            fixture.AssertRestored(1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Add_members_reverts_if_regeneration_rotates_the_centered_witness_and_the_addition(bool apply)
        {
            var fixture = new Fixture(quarterTurn: true, singleCenteredMember: true);
            if (apply) fixture.Rehearse();
            fixture.Document.LoseGroupRotation = true;

            CommandResult result = fixture.Execute();

            Assert.False(result.Success);
            JObject attempted = apply ? result.Detail : Assert.IsType<JObject>(result.Detail["rehearsal"]);
            Assert.False(Check(attempted, "copy_additions").Value<bool>("matches"));
            var placement = CopyAdditions(attempted)["placement"];
            Assert.Equal(90, placement.Value<double>("rotation_degrees"), 8);
            Assert.Contains("pre-regeneration", placement.Value<string>("reason"));
            Assert.Null(result.Detail.SelectToken("$..confirmation_token"));
            fixture.AssertRestored(apply ? 2 : 1);
        }

        [Fact]
        public void Add_members_withholds_the_token_when_curves_cannot_distinguish_a_reflected_copy()
        {
            var fixture = new Fixture(reflectedCopy: true);
            fixture.DeclareRotation(null);
            fixture.Document.TranslateNewGroupMember = true;
            var copy = (Group)fixture.Document.Elements[12];
            var horizontal = AxisMember(21, new XYZ(-1, 0, 0), new XYZ(1, 0, 0));
            var vertical = AxisMember(22, new XYZ(0, -1, 0), new XYZ(0, 1, 0));
            fixture.ReplaceMember(21, horizontal);
            fixture.ReplaceMember(22, vertical);
            fixture.ReplaceMember(30, copy.PlaceMember(horizontal, Rid.Make(30)));
            var mirrored = copy.PlaceMember(vertical, Rid.Make(31));
            var location = (LocationCurve)mirrored.Location;
            location.Curve = Line.CreateBound(location.Curve.GetEndPoint(1), location.Curve.GetEndPoint(0));
            fixture.ReplaceMember(31, mirrored);
            fixture.Document.AfterGroupRegeneration = doc =>
                ((LocationCurve)doc.Elements[51].Location).Curve = location.Curve;

            CommandResult result = fixture.Execute();

            Assert.False(result.Success);
            Assert.Contains("relative_rotations_degrees", result.Error);
            Assert.Equal(new[] { 12L }, result.Detail["copy_group_ids"].Values<long>());
            Assert.Null(result.Detail.SelectToken("$..confirmation_token"));
            fixture.AssertRestored(0);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Add_members_rolls_back_an_arc_replaced_by_the_opposite_quarter_circle(bool apply)
        {
            var fixture = new Fixture();
            fixture.ReplaceMember(23, QuarterCircleMember());
            if (apply) fixture.Rehearse();
            fixture.Document.BeforeTransactionObservation = doc =>
                ((LocationCurve)doc.Elements[52].Location).Curve = QuarterCircle(30, opposite: true);

            CommandResult result = fixture.Execute();

            AssertCurveRollback(fixture, result, apply, false);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Add_members_rolls_back_when_near_full_arcs_diverge_between_endpoints_and_midpoint(bool apply)
        {
            var fixture = new Fixture();
            const double gap = 0.00001, tilt = 0.005;
            var reference = InclinedArc(gap, 2 * Math.PI - gap, tilt);
            fixture.ReplaceMember(23, CurveMember(23, reference, new XYZ(-10, -10, -10), new XYZ(10, 10, 10)));
            if (apply) fixture.Rehearse();
            XYZ origin = ((Group)fixture.Document.Elements[12]).Location.Point;
            fixture.Document.BeforeTransactionObservation = doc =>
                ((LocationCurve)doc.Elements[52].Location).Curve = MoveArc(InclinedArc(gap, 2 * Math.PI - gap, -tilt), origin);

            CommandResult result = fixture.Execute();

            AssertCurveRollback(fixture, result, apply, false);
        }

        [Fact]
        public void Add_members_rolls_back_a_curves_only_reflected_copy_with_a_declared_angle()
        {
            var fixture = new Fixture(reflectedCopy: true);
            var copy = (Group)fixture.Document.Elements[12];
            var horizontal = AxisMember(21, new XYZ(-1, 0, 0), new XYZ(1, 0, 0));
            var vertical = AxisMember(22, new XYZ(0, -1, 0), new XYZ(0, 1, 0));
            fixture.ReplaceMember(21, horizontal);
            fixture.ReplaceMember(22, vertical);
            fixture.ReplaceMember(30, copy.PlaceMember(horizontal, Rid.Make(30)));
            fixture.ReplaceMember(31, copy.PlaceMember(vertical, Rid.Make(31)));
            fixture.ReplaceMember(23, CurveMember(23, CircularArc(5, 0, 5, Math.PI, 0),
                new XYZ(0, -5, 0), new XYZ(10, 5, 3)));

            CommandResult result = fixture.Execute();

            AssertCurveRollback(fixture, result, false, false);
        }

        [Fact]
        public void Add_members_verifies_an_arc_after_rotation_and_parameter_reversal()
        {
            var fixture = new Fixture(quarterTurn: true);
            fixture.ReplaceMember(23, QuarterCircleMember());
            fixture.Document.BeforeTransactionObservation = doc =>
            {
                var location = (LocationCurve)doc.Elements[52].Location;
                var forward = (Arc)location.Curve;
                location.Curve = new Arc(t => forward.Evaluate(1 - t, true), forward.Center, forward.Length / forward.Radius);
            };
            fixture.Rehearse();

            CommandResult result = fixture.Execute();

            Assert.True(result.Success, result.Error);
            JObject data = Assert.IsType<JObject>(result.Data);
            Assert.True(CopyAdditions(data)["placement"]["members"][0].Value<bool>("curve_path_matches"));
            CommandResult variation = fixture.Execute(new JObject { ["operation"] = "check_variation",
                ["group_ids"] = data["result"]["affected_group_ids"], ["relative_rotations_degrees"] = new JArray(90) });
            Assert.True(variation.Success, variation.Error);
            JObject report = Assert.IsType<JObject>(variation.Data);
            Assert.True(report.Value<bool>("coverage_complete"));
            Assert.Equal(0, report.Value<int>("variation_count"));
        }

        [Fact]
        public void Add_members_withholds_the_token_if_the_curve_path_cannot_be_measured()
        {
            var fixture = new Fixture();
            fixture.ReplaceMember(23, QuarterCircleMember());
            fixture.Document.BeforeTransactionObservation = doc =>
            {
                var location = (LocationCurve)doc.Elements[52].Location;
                location.Curve = new Curve { A = location.Curve.GetEndPoint(0), B = location.Curve.GetEndPoint(1) };
            };

            CommandResult result = fixture.Execute();

            AssertCurveRollback(fixture, result, false, null);
        }

        [Fact]
        public void Add_members_rolls_back_when_only_a_retained_arc_changes_path()
        {
            var fixture = new Fixture();
            var source = QuarterCircleMember(21);
            fixture.ReplaceMember(21, source);
            var copy = (Group)fixture.Document.Elements[12];
            fixture.ReplaceMember(30, copy.PlaceMember(source, Rid.Make(30)));
            fixture.Document.BeforeTransactionObservation = doc =>
                ((LocationCurve)doc.Elements[50].Location).Curve = QuarterCircle(30, opposite: true);

            CommandResult result = fixture.Execute();

            Assert.False(result.Success);
            JObject rehearsal = Assert.IsType<JObject>(result.Detail["rehearsal"]);
            var placement = CopyAdditions(rehearsal)["placement"];
            Assert.Contains("retained member's curve path", placement.Value<string>("reason"));
            Assert.Equal(21L, placement.Value<long>("unverified_source_member_id"));
            Assert.True(rehearsal.Value<bool>("rollback_confirmed"));
            Assert.Null(result.Detail.SelectToken("$..confirmation_token"));
            fixture.AssertRestored(1);
        }

        [Fact]
        public void Add_members_token_is_stale_if_the_source_arc_changes_with_the_same_bounds_and_chord()
        {
            var fixture = new Fixture();
            var source = QuarterCircleMember();
            fixture.ReplaceMember(23, source);
            fixture.Rehearse();
            ((LocationCurve)source.Location).Curve = QuarterCircle(0, opposite: true);

            CommandResult result = fixture.Execute();

            Assert.False(result.Success);
            Assert.Equal("stale_plan", result.Detail.Value<string>("state"));
            fixture.AssertRestored(1);
        }

        [Theory]
        [InlineData("type")]
        [InlineData("all_instances")]
        public void Add_members_assigns_angles_by_ascending_copy_id_even_when_copies_are_enumerated_in_reverse(string scope)
        {
            var fixture = new Fixture(scope);
            fixture.AddQuarterTurnCopyWithLowerId();
            fixture.DeclareRotation(null);
            CommandResult missingAngles = fixture.Execute();
            Assert.False(missingAngles.Success);
            Assert.Equal(new long[] { 9, 12 }, missingAngles.Detail["copy_group_ids"].Values<long>());
            fixture.AssertRestored(0);
            fixture.DeclareRotations(90, 0);
            fixture.Rehearse();

            CommandResult result = fixture.Execute();

            Assert.True(result.Success, result.Error);
            var copies = AllCopyAdditions(Assert.IsType<JObject>(result.Data));
            Assert.Equal(new long[] { 12, 9 }, copies.Select(copy => copy.Value<long>("group_id")));
            Assert.Equal(new double[] { 0, 90 }, copies.Select(copy => copy["placement"].Value<double>("rotation_degrees")));
            Assert.Equal(new long[] { 52, 62 }, copies.Select(copy => copy["added_member_ids"].Single().Value<long>()));
        }

        private static void AssertCurveRollback(Fixture fixture, CommandResult result, bool apply, bool? matches)
        {
            Assert.False(result.Success);
            JObject attempted = apply ? result.Detail : Assert.IsType<JObject>(result.Detail["rehearsal"]);
            if (apply) Assert.Equal("rolled_back", attempted.Value<string>("state"));
            else Assert.True(attempted.Value<bool>("rollback_confirmed"));
            var placement = CopyAdditions(attempted)["placement"];
            Assert.Equal(matches.HasValue, placement.Value<bool>("coverage_complete"));
            Assert.True(placement["members"][0].Value<bool>("geometry_matches"));
            Assert.False(placement.Value<bool>("matches"));
            Assert.Equal(matches, placement["members"][0].Value<bool?>("curve_path_matches"));
            Assert.Null(result.Detail.SelectToken("$..confirmation_token"));
            fixture.AssertRestored(apply ? 2 : 1);
        }

        private static Arc CircularArc(double x, double y, double radius, double start, double end) =>
            new Arc(t =>
            {
                double angle = start + t * (end - start);
                return new XYZ(x + radius * Math.Cos(angle), y + radius * Math.Sin(angle), 0);
            }, new XYZ(x, y, 0), Math.Abs(end - start));

        private static Element QuarterCircleMember(long id = 23) =>
            CurveMember(id, QuarterCircle(0), new XYZ(0, 0, 0), new XYZ(10, 10, 3));

        private static Arc QuarterCircle(double x, bool opposite = false)
        {
            Arc arc = opposite
                ? CircularArc(x, 10, 10, -Math.PI / 2, 0)
                : CircularArc(x + 10, 0, 10, Math.PI, Math.PI / 2);
            arc.A = new XYZ(x, 0, 0);
            arc.B = new XYZ(x + 10, 10, 0);
            return arc;
        }

        private static Element CurveMember(long id, Curve curve, XYZ min, XYZ max) =>
            new Element { Id = Rid.Make(id), Bounds = new BoundingBoxXYZ { Min = min, Max = max },
                Location = new LocationCurve { Curve = curve } };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Variation_distinguishes_changed_curvature_from_an_unreadable_curve_path(bool unreadable)
        {
            var fixture = new Fixture();
            var source = QuarterCircleMember(21);
            fixture.ReplaceMember(21, source);
            Group copy = (Group)fixture.Document.Elements[12];
            Element counterpart = copy.PlaceMember(source, Rid.Make(30));
            Arc opposite = QuarterCircle(30, opposite: true);
            ((LocationCurve)counterpart.Location).Curve = unreadable ? new Curve { A = opposite.A, B = opposite.B } : opposite;
            fixture.ReplaceMember(30, counterpart);

            CommandResult result = fixture.Execute(new JObject { ["operation"] = "check_variation",
                ["group_ids"] = new JArray(10, 12), ["relative_rotations_degrees"] = new JArray(0) });

            Assert.True(result.Success, result.Error);
            JObject report = Assert.IsType<JObject>(result.Data);
            Assert.Equal(!unreadable, report.Value<bool>("coverage_complete"));
            Assert.Equal(unreadable ? (int?)null : 1, report.Value<int?>("variation_count"));
            Assert.Equal(unreadable ? (bool?)null : true, report.Value<bool?>("has_variation"));
            Assert.Equal(unreadable ? (bool?)null : false, report["instances"][0]["members"][0].Value<bool?>("curve_path_matches"));
        }

        [Theory]
        [InlineData(0.0002, Math.PI / 4, 1)]
        [InlineData(0.005, 0.005, 0.3048)]
        public void Variation_detects_different_arc_planes_even_when_the_bounds_endpoints_and_midpoint_agree(
            double gap, double tilt, double toleranceMm)
        {
            Arc reference = InclinedArc(gap, 2 * Math.PI - gap, tilt);
            Arc copy = InclinedArc(gap, 2 * Math.PI - gap, -tilt);
            double toleranceFeet = toleranceMm / 304.8;
            foreach (double t in new[] { 0.0, 0.5, 1.0 })
                Assert.True(reference.Evaluate(t, true).DistanceTo(copy.Evaluate(t, true)) < toleranceFeet);
            Assert.True(reference.Evaluate(0.25, true).DistanceTo(copy.Evaluate(0.25, true)) > toleranceFeet);

            JObject result = CompareArcVariation(reference, copy, toleranceMm);

            Assert.True(result.Value<bool>("coverage_complete"));
            Assert.True(result.Value<bool>("has_variation"));
            Assert.Equal(1, result.Value<int>("variation_count"));
            Assert.False(result["instances"][0]["members"][0].Value<bool>("curve_path_matches"));
        }

        [Theory]
        [InlineData(0.5, true)]
        [InlineData(1.03, null)]
        public void Variation_only_confirms_coincident_arcs_when_the_entire_path_fits_the_tolerance(
            double peakInTolerances, bool? matches)
        {
            const double toleranceMm = 1;
            double toleranceFeet = toleranceMm / 304.8;
            double tilt = Math.Asin(peakInTolerances * toleranceFeet / 20);
            Arc reference = InclinedArc(0, Math.PI, tilt, Math.PI / 8);
            Arc copy = InclinedArc(0, Math.PI, -tilt, Math.PI / 8);
            foreach (double t in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
                Assert.True(reference.Evaluate(t, true).DistanceTo(copy.Evaluate(t, true)) < toleranceFeet);
            Assert.Equal(peakInTolerances * toleranceFeet, reference.Evaluate(0.625, true).DistanceTo(copy.Evaluate(0.625, true)), 10);

            JObject result = CompareArcVariation(reference, copy, toleranceMm);

            Assert.Equal(matches.HasValue, result.Value<bool>("coverage_complete"));
            Assert.Equal(matches == true ? (int?)0 : null, result.Value<int?>("variation_count"));
            Assert.Equal(matches == true ? (bool?)false : null, result.Value<bool?>("has_variation"));
            Assert.Equal(matches, result["instances"][0]["members"][0].Value<bool?>("curve_path_matches"));
            if (!matches.HasValue) Assert.Contains("within the tolerance", result["instances"][0].Value<string>("reason"));
        }

        private static Arc InclinedArc(double start, double end, double tilt, double axis = 0) => new Arc(t =>
        {
            double angle = start + t * (end - start) - axis;
            double along = 10 * Math.Cos(angle), across = 10 * Math.Sin(angle);
            return new XYZ(along * Math.Cos(axis) - across * Math.Cos(tilt) * Math.Sin(axis),
                along * Math.Sin(axis) + across * Math.Cos(tilt) * Math.Cos(axis), across * Math.Sin(tilt));
        }, new XYZ(0, 0, 0), end - start);

        private static JObject CompareArcVariation(Arc reference, Arc copy, double toleranceMm)
        {
            var fixture = new Fixture();
            var group = (Group)fixture.Document.Elements[12];
            var source = CurveMember(21, reference, new XYZ(-10, -10, -10), new XYZ(10, 10, 10));
            fixture.ReplaceMember(21, source);
            Element counterpart = group.PlaceMember(source, Rid.Make(30));
            ((LocationCurve)counterpart.Location).Curve = MoveArc(copy, group.Location.Point);
            fixture.ReplaceMember(30, counterpart);

            CommandResult result = fixture.Execute(new JObject { ["operation"] = "check_variation",
                ["group_ids"] = new JArray(10, 12), ["relative_rotations_degrees"] = new JArray(0), ["tolerance_mm"] = toleranceMm });

            Assert.True(result.Success, result.Error);
            JObject report = Assert.IsType<JObject>(result.Data);
            Assert.True(report["instances"][0]["members"][0].Value<bool>("geometry_matches"));
            return report;
        }

        private static Arc MoveArc(Arc arc, XYZ translation) =>
            new Arc(t => translation + arc.Evaluate(t, true), translation + arc.Center, arc.Length / arc.Radius);

        [Fact]
        public void Add_members_does_not_treat_a_preexisting_family_rotation_as_the_group_rotation()
        {
            var fixture = new Fixture(singleCenteredMember: true);
            var witness = (FamilyInstance)fixture.Document.Elements[30];
            TurnFamily(witness, 1);
            fixture.Document.AfterGroupRegeneration = doc =>
            {
                // Preserve the variation of the retained family and simulate an addition
                // rotated about the copy origin as if the whole group had turned with it.
                TurnFamily((FamilyInstance)doc.Elements[50], 1);
                doc.Elements[52].Bounds = new BoundingBoxXYZ { Min = new XYZ(28, 10, 0), Max = new XYZ(30, 11, 3) };
                ((LocationCurve)doc.Elements[52].Location).Curve =
                    Line.CreateBound(new XYZ(30, 10, 0), new XYZ(28, 10, 0));
            };

            CommandResult result = fixture.Execute();

            Assert.False(result.Success);
            var placement = CopyAdditions(Assert.IsType<JObject>(result.Detail["rehearsal"]))["placement"];
            Assert.Equal("declared", placement.Value<string>("rotation_source"));
            Assert.False(placement["members"][0].Value<bool>("geometry_matches"));
            Assert.Null(result.Detail.SelectToken("$..confirmation_token"));
            fixture.AssertRestored(1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Add_members_reverts_if_only_the_centered_square_addition_loses_its_rotation(bool apply)
        {
            var fixture = new Fixture(quarterTurn: true, singleCenteredMember: true);
            fixture.ReplaceMember(23, new FamilyInstance { Id = Rid.Make(23), Bounds = SquareAt(0, 0) });
            if (apply) fixture.Rehearse();
            fixture.Document.BeforeTransactionObservation = doc => TurnFamily((FamilyInstance)doc.Elements[52], 0);

            CommandResult result = fixture.Execute();

            Assert.False(result.Success);
            JObject attempted = apply ? result.Detail : Assert.IsType<JObject>(result.Detail["rehearsal"]);
            var placement = CopyAdditions(attempted)["placement"];
            Assert.True(placement["members"][0].Value<bool>("geometry_matches"));
            Assert.False(placement["members"][0].Value<bool>("orientation_matches"));
            Assert.False(placement.Value<bool>("matches"));
            Assert.Equal(23L, placement.Value<long>("unverified_source_member_id"));
            Assert.Null(result.Detail.SelectToken("$..confirmation_token"));
            fixture.AssertRestored(apply ? 2 : 1);
        }

        private static void TurnFamily(FamilyInstance family, int quarterTurn)
        {
            family.Placement.BasisX = new XYZ(1 - quarterTurn, quarterTurn, 0);
            family.Placement.BasisY = new XYZ(-quarterTurn, 1 - quarterTurn, 0);
        }

        private static Element AxisMember(long id, XYZ start, XYZ end) =>
            new Element { Id = Rid.Make(id), Bounds = SquareAt(0, 0),
                Location = new LocationCurve { Curve = Line.CreateBound(start, end) } };

        [Fact]
        public void Add_members_token_is_stale_if_the_original_witness_turns_before_apply()
        {
            var fixture = new Fixture(quarterTurn: true, singleCenteredMember: true);
            fixture.Rehearse();
            ((FamilyInstance)fixture.Document.Elements[30]).Placement.BasisX = new XYZ(1, 0, 0);

            CommandResult result = fixture.Execute();

            Assert.False(result.Success);
            Assert.Equal("stale_plan", result.Detail.Value<string>("state"));
            fixture.AssertRestored(1);
        }

        [Fact]
        public void Add_members_token_is_stale_if_the_declared_rotation_changes()
        {
            var fixture = new Fixture(quarterTurn: true, singleCenteredMember: true);
            fixture.Rehearse();
            fixture.DeclareRotation(0);

            CommandResult result = fixture.Execute();

            Assert.False(result.Success);
            fixture.AssertRestored(1);
        }

        [Theory]
        [InlineData("type")]
        [InlineData("all_instances")]
        public void Add_members_with_no_readable_family_axis_withholds_the_token(string scope)
        {
            var fixture = new Fixture(scope, quarterTurn: true, singleCenteredMember: true, readAxes: false);
            fixture.Document.TranslateNewGroupMember = true;

            CommandResult result = fixture.Execute();

            Assert.False(result.Success);
            JObject rehearsal = Assert.IsType<JObject>(result.Detail["rehearsal"]);
            Assert.True(rehearsal.Value<bool>("rollback_confirmed"));
            Assert.False(Check(rehearsal, "copy_additions").Value<bool>("matches"));
            var placement = CopyAdditions(rehearsal)["placement"];
            Assert.False(placement.Value<bool>("coverage_complete"));
            Assert.False(placement.Value<bool>("matches"));
            Assert.Contains("pre-regeneration", placement.Value<string>("reason"));
            Assert.Null(result.Detail.SelectToken("$..confirmation_token"));
            fixture.AssertRestored(1);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Variation_does_not_treat_a_displaced_square_as_a_group_rotation(bool knownRotation)
        {
            var fixture = new Fixture();
            var reference = (Group)fixture.Document.Elements[10];
            var copy = (Group)fixture.Document.Elements[12];
            copy.Location.Point = reference.Location.Point;
            fixture.Document.Elements[21].Bounds = SquareAt(1, 0);
            fixture.Document.Elements[30].Bounds = SquareAt(0, 1);
            fixture.Document.Elements[22].Bounds = SquareAt(2, 0);
            fixture.Document.Elements[31].Bounds = SquareAt(0, 2);
            ((FamilyInstance)fixture.Document.Elements[30]).Placement.BasisX = new XYZ(0, 1, 0);
            ((FamilyInstance)fixture.Document.Elements[31]).Placement.BasisX = new XYZ(0, 1, 0);

            var request = new JObject { ["operation"] = "check_variation", ["group_ids"] = new JArray(10, 12) };
            if (knownRotation) request["relative_rotations_degrees"] = new JArray(0);
            CommandResult result = fixture.Execute(request);

            Assert.True(result.Success, result.Error);
            JObject observed = Assert.IsType<JObject>(result.Data);
            Assert.Equal(knownRotation, observed.Value<bool>("coverage_complete"));
            if (knownRotation)
            {
                Assert.Equal(1, observed.Value<int>("variation_count"));
                Assert.True(observed.Value<bool>("has_variation"));
                Assert.All(observed["instances"][0]["members"], m => Assert.False(m.Value<bool>("geometry_matches")));
            }
            else
            {
                Assert.Equal(JTokenType.Null, observed["variation_count"].Type);
                Assert.Equal(JTokenType.Null, observed["has_variation"].Type);
                Assert.Contains("does not expose", observed["instances"][0].Value<string>("reason"));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Variation_reports_known_type_or_membership_differences_without_rotation(bool missingMember)
        {
            var fixture = new Fixture();
            var copy = (Group)fixture.Document.Elements[12];
            if (missingMember) copy.Members.RemoveAt(1);
            else fixture.Document.Elements[31] = new DifferentType { Id = Rid.Make(31), Bounds = fixture.Document.Elements[31].Bounds };

            CommandResult result = fixture.Execute(new JObject { ["operation"] = "check_variation", ["group_ids"] = new JArray(10, 12) });

            Assert.True(result.Success, result.Error);
            JObject report = Assert.IsType<JObject>(result.Data);
            Assert.False(report.Value<bool>("coverage_complete"));
            Assert.True(report.Value<bool>("has_variation"));
            Assert.Equal(JTokenType.Null, report["variation_count"].Type);
            var row = report["instances"][0]["members"][1];
            if (missingMember) Assert.Equal(JTokenType.Null, row["member_id"].Type);
            else Assert.False(row.Value<bool>("type_matches"));
            Assert.Equal(JTokenType.Null, row["geometry_matches"].Type);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(100)]
        public void Variation_preserves_measured_height_when_oblique_plan_geometry_is_missing(int heightDeltaMm)
        {
            var fixture = new Fixture(singleCenteredMember: true);
            var copy = (Group)fixture.Document.Elements[12];
            copy.Location.Point += new XYZ(0, 0, 4);
            BoundingBoxXYZ bounds = fixture.Document.Elements[30].Bounds;
            bounds.Min += new XYZ(0, 0, 4);
            bounds.Max += new XYZ(0, 0, 4 + heightDeltaMm / 304.8);

            CommandResult result = fixture.Execute(new JObject { ["operation"] = "check_variation",
                ["group_ids"] = new JArray(10, 12), ["relative_rotations_degrees"] = new JArray(45) });

            Assert.True(result.Success, result.Error);
            JObject report = Assert.IsType<JObject>(result.Data);
            Assert.False(report.Value<bool>("coverage_complete"));
            Assert.Equal(JTokenType.Null, report["variation_count"].Type);
            Assert.Equal(heightDeltaMm > 0 ? true : (bool?)null, report.Value<bool?>("has_variation"));
            JToken row = report["instances"][0]["members"][0];
            Assert.Equal(heightDeltaMm == 0, row.Value<bool>("height_matches"));
            Assert.Equal(heightDeltaMm, row.Value<double>("top_mm") - row.Value<double>("expected_top_mm"), 8);
            Assert.Equal(JTokenType.Null, row["geometry_matches"].Type);
        }

        private sealed class DifferentType : Element { public override ElementId GetTypeId() => Rid.Make(2); }

        private static BoundingBoxXYZ SquareAt(double x, double y) => new BoundingBoxXYZ
        {
            Min = new XYZ(x - 0.25, y - 0.25, 0), Max = new XYZ(x + 0.25, y + 0.25, 3)
        };

        [Fact]
        public void Add_members_preserves_the_commit_when_the_last_copy_membership_read_throws()
        {
            var fixture = new Fixture();
            fixture.Rehearse();
            fixture.Document.FailGroupMemberReadAfterAssimilation = true;
            fixture.Document.IsWorkshared = true;
            using var changes = new ChangeWatch(fixture.Document.Application);

            CommandResult result = fixture.Execute();

            Assert.False(result.Success);
            Assert.True(fixture.Document.Assimilated);
            Assert.Equal(1, fixture.Document.TransactionGroupRollbacks);
            Assert.Equal(3, ((Group)fixture.Document.Elements[12]).Members.Count);
            Assert.Equal("postcommit_verification_failed", result.Detail.Value<string>("code"));
            Assert.Equal("Committed", result.Detail.Value<string>("transaction_status"));
            Assert.Equal("Committed", result.Detail.Value<string>("transaction_group_status"));
            Assert.True(result.Detail.Value<bool>("write_started"));
            Assert.True(result.Detail.Value<bool>("changes_applied"));
            Assert.False(result.Detail.Value<bool>("host_verified"));
            Assert.Contains("Could not reread copy 12", result.Detail.Value<string>("verification_error"));
            Assert.Equal("uncertain", result.Detail["application"].Value<string>("state"));
            Assert.Equal(1, result.Detail["application"].Value<int>("applied"));
            Assert.Equal(0, result.Detail["application"].Value<int>("verified"));
            Assert.Null(result.Detail.SelectToken("$..also_created"));
            fixture.RecordSyncResult(result, changes);
            var blocker = Assert.Single(SyncLedgerRules.Blockers(fixture.Document.Title));
            Assert.Equal("postcommit_verification_failed", blocker.BlockReason);
            Assert.Contains(52L, blocker.Ids);
            SyncLedgerRules.MarkSynced(fixture.Document.Title, DateTime.UtcNow);
        }

        private static JObject Check(JObject result, string property) =>
            Assert.Single(result["postconditions"]["properties"].OfType<JObject>(), row => row.Value<string>("property") == property);

        private static IEnumerable<JObject> AllCopyAdditions(JObject result) =>
            Check(result, "copy_additions")["found_in_committed_model"].Values<JObject>();

        private static JObject CopyAdditions(JObject result) => Assert.Single(AllCopyAdditions(result));

        private sealed class Fixture
        {
            public Document Document { get; } = new Document
            {
                Title = "group-test-" + Guid.NewGuid().ToString("N"), CreationConfirmations = DocumentGate.Confirmations
            };
            private readonly UIApplication _app;
            private readonly ManageGroupsCommand _command = new ManageGroupsCommand();
            private readonly JObject _request = new JObject
            {
                ["operation"] = "add_members", ["scope"] = "type", ["group_ids"] = new JArray(10), ["element_ids"] = new JArray(23)
            };
            private readonly List<Element> _originals;

            public Fixture(string scope = "type", bool quarterTurn = false, bool singleCenteredMember = false, bool readAxes = true,
                bool reflectedCopy = false)
            {
                _request["scope"] = scope;
                DeclareRotation(quarterTurn ? 90 : 0);
                double copyX = quarterTurn ? 10 : 30;
                _app = new UIApplication { ActiveUIDocument = new UIDocument { Document = Document }, Application = Document.Application };
                var type = new GroupType { Id = Rid.Make(11) };
                var reference = GroupAt(10, type, 0, singleCenteredMember ? new[] { 21L } : new[] { 21L, 22L });
                var copy = GroupAt(12, type, copyX, singleCenteredMember ? new[] { 30L } : new[] { 30L, 31L });
                copy.TestQuarterTurn = quarterTurn;
                copy.TestReflected = reflectedCopy;
                var first = MemberAt(21, 0);
                if (singleCenteredMember)
                {
                    first.Bounds = SquareAt(0, 0);
                    first.Location = new LocationPoint { Point = new XYZ(0, 0, 0) };
                }
                if (!readAxes) ((FamilyInstance)first).Placement.BasisX = null;
                var second = MemberAt(22, 5);
                _originals = new List<Element>
                {
                    type, reference, copy, first, second, CurveMemberAt(23, quarterTurn ? 1 : 10),
                    copy.PlaceMember(first, Rid.Make(30)), copy.PlaceMember(second, Rid.Make(31)), MemberAt(90, 100)
                };
                foreach (Element element in _originals) Document.Elements.Add(element.Id.Value, element);
            }

            private static Group GroupAt(long id, GroupType type, double x, params long[] members) =>
                new Group { Id = Rid.Make(id), GroupType = type, Location = new LocationPoint { Point = new XYZ(x, 0, 0) },
                    Members = members.Select(Rid.Make).ToList() };
            private static Element MemberAt(long id, double x) =>
                new FamilyInstance { Id = Rid.Make(id), Bounds = new BoundingBoxXYZ { Min = new XYZ(x, 0, 0), Max = new XYZ(x + 1, 2, 3) } };

            private static Element CurveMemberAt(long id, double x) =>
                new Element { Id = Rid.Make(id), Bounds = new BoundingBoxXYZ { Min = new XYZ(x, 0, 0), Max = new XYZ(x + 1, 2, 3) },
                    Location = new LocationCurve { Curve = Line.CreateBound(new XYZ(x, 0, 0), new XYZ(x, 2, 0)) } };

            public CommandResult Execute(JObject request = null) => _command.Execute(_app, (request ?? _request).ToString());

            public void DeclareRotation(double? degrees)
            {
                if (degrees.HasValue) DeclareRotations(degrees.Value);
                else _request.Remove("relative_rotations_degrees");
            }

            public void DeclareRotations(params double[] angles) => _request["relative_rotations_degrees"] = new JArray(angles);

            public void AddQuarterTurnCopyWithLowerId()
            {
                var reference = (Group)Document.Elements[10];
                Group copy = GroupAt(9, reference.GroupType, 60, 70, 71);
                copy.TestQuarterTurn = true;
                var members = reference.Members.Select((id, index) => copy.PlaceMember(Document.GetElement(id), copy.Members[index]));
                foreach (Element element in members.Prepend(copy))
                {
                    _originals.Add(element);
                    Document.Elements.Add(element.Id.Value, element);
                }
            }

            public void ReplaceMember(long id, Element member)
            {
                _originals[_originals.FindIndex(original => original.Id.Value == id)] = member;
                Document.Elements[id] = member;
            }

            public void RecordSyncResult(CommandResult result, ChangeWatch changes) =>
                SyncReminder.AfterCommand(_app, _command.Name, _request.ToString(), changes, result);

            public void Rehearse()
            {
                CommandResult result = Execute();
                Assert.True(result.Success, result.Error);
                JObject data = Assert.IsType<JObject>(result.Data);
                Assert.True(data["rehearsal"].Value<bool>("rollback_confirmed"));
                AssertRestored(1);
                string token = data.Value<string>("confirmation_token");
                Assert.False(string.IsNullOrEmpty(token));
                _request["confirmation_token"] = token;
                _request["dry_run"] = false;
                _request["idempotency_key"] = Guid.NewGuid().ToString("N");
            }

            public void AssertRestored(int rollbacks)
            {
                Assert.False(Document.Assimilated);
                Assert.Equal(rollbacks, Document.TransactionGroupRollbacks);
                Assert.Equal(_originals.Count, Document.Elements.Count);
                Assert.All(_originals, original => Assert.Same(original, Document.GetElement(original.Id)));
            }
        }
    }
}
