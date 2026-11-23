using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class ObliquePipeTeeRulesTests
    {
        [Fact]
        public void A_forty_five_degree_branch_requires_the_oblique_route()
        {
            var run = new[] { 1.0, 0.0, 0.0 };
            var branch = new[] { 1.0, 1.0, 0.0 };
            double angle = ObliquePipeTeeRules.AcuteBranchAngleDegrees(run, branch);
            Assert.Equal(45.0, angle, 6);
            Assert.True(ObliquePipeTeeRules.NeedsObliqueWorkaround(angle));
            Assert.Equal(new[] { 0.0, 1.0, 0.0 }, ObliquePipeTeeRules.PerpendicularTowardsBranch(run, branch));
        }

        [Fact]
        public void Orthogonal_branch_uses_the_ordinary_tee_and_reverse_main_is_equivalent()
        {
            var branch = new[] { -1.0, 1.0, 0.0 };
            Assert.Equal(45.0, ObliquePipeTeeRules.AcuteBranchAngleDegrees(new[] { -1.0, 0.0, 0.0 }, branch), 6);
            Assert.False(ObliquePipeTeeRules.NeedsObliqueWorkaround(
                ObliquePipeTeeRules.AcuteBranchAngleDegrees(new[] { 1.0, 0.0, 0.0 }, new[] { 0.0, 1.0, 0.0 })));
            Assert.Throws<ArgumentException>(() => ObliquePipeTeeRules.PerpendicularTowardsBranch(
                new[] { 1.0, 0.0, 0.0 }, new[] { 2.0, 0.0, 0.0 }));
        }

        [Fact]
        public void Temporary_pipe_extends_back_towards_the_real_pipe_from_its_connector()
        {
            // The connector at the junction points into the fitting (+Y), while
            // the real pipe and the replacement temporary pipe occupy the -Y side.
            var mainConnector = new[] { 1.0, 0.0, 0.0 };
            var branchConnector = new[] { 1.0, 1.0, 0.0 };
            Assert.Equal(new[] { 0.0, -1.0, 0.0 },
                ObliquePipeTeeRules.PerpendicularPipeExtensionFromConnector(mainConnector, branchConnector));
        }
        [Fact]
        public void A_wye_port_beside_the_drawn_branch_slides_along_the_run_onto_it()
        {
            // Run along +X, drawn branch at 45 degrees through the origin. The port sits
            // 162 mm off that axis; sliding along X by 162*sqrt(2) puts it back on.
            var run = new[] { 1.0, 0.0, 0.0 };
            var d = new[] { -1.0, 1.0, 0.0 };
            double off = 162.0;
            var onAxis = new[] { -300.0, 300.0, 0.0 };
            var port = new[] { onAxis[0] + off / Math.Sqrt(2), onAxis[1] + off / Math.Sqrt(2), 0.0 };
            double t = ObliquePipeTeeRules.SlideAlongRunToMeetBranch(run, port, new[] { 0.0, 0.0, 0.0 }, d).Value;
            Assert.Equal(-off * Math.Sqrt(2), t, 6);
            var moved = new[] { port[0] + t, port[1], port[2] };
            Assert.Equal(moved[0], -moved[1], 6);
        }

        [Fact]
        public void A_port_already_on_the_branch_does_not_slide_and_a_parallel_branch_is_refused()
        {
            var run = new[] { 2.0, 0.0, 0.0 };
            Assert.Equal(0.0, ObliquePipeTeeRules.SlideAlongRunToMeetBranch(run, new[] { -5.0, 5.0, 0.0 },
                new[] { 0.0, 0.0, 0.0 }, new[] { -1.0, 1.0, 0.0 }).Value, 9);
            Assert.Null(ObliquePipeTeeRules.SlideAlongRunToMeetBranch(run, new[] { 0.0, 3.0, 0.0 },
                new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0.0, 0.0 }));
        }
            [Fact]
        public void A_vertical_wye_is_rolled_flat_then_slid_onto_the_drawn_branch()
        {
            // Run along +X through the origin; wye origin 0.5 ft along the run; its port leans 45 deg
            // forward but UP (+Z) with the port 0.3 ft from the origin. Drawn branch is flat (+Y) from junction at x=0.
            double s2 = System.Math.Sqrt(0.5);
            var run = new[] { 1.0, 0.0, 0.0 };
            var origin = new[] { 0.5, 0.0, 0.0 };
            var portDir = new[] { s2, 0.0, s2 };
            var port = new[] { 0.5 + 0.3 * s2, 0.0, 0.3 * s2 };
            var junction = new[] { 0.0, 0.0, 0.0 };
            var far = new[] { s2, s2, 0.0 };
            var r = ObliquePipeTeeRules.SolveRollAndSlide(run, origin, port, portDir, junction, far);
            Assert.Null(r.Problem);
            Assert.True(r.ResidualFeet < 1e-9, "residual " + r.ResidualFeet);
            Assert.True(System.Math.Abs(r.RollRadians) > 1.0);
            Assert.True(System.Math.Abs(r.SlideFeet) > 0.1);
            var rolled = ObliquePipeTeeRules.Rotate(new[] { portDir[0], portDir[1], portDir[2] }, run, r.RollRadians);
            Assert.True(ObliquePipeTeeRules.PortFacesBranch(rolled, far, 0.5));
        }

        [Fact]
        public void A_ninety_degree_port_cannot_be_slid_onto_a_forty_five_degree_branch()
        {
            var r = ObliquePipeTeeRules.SolveRollAndSlide(new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 0 }, new[] { 0.0, 0.3, 0 },
                new[] { 0.0, 1.0, 0 }, new[] { 0.0, 0, 0 }, new[] { System.Math.Sqrt(0.5), System.Math.Sqrt(0.5), 0.0 });
            Assert.NotNull(r.Problem);
            Assert.False(ObliquePipeTeeRules.PortFacesBranch(new[] { 0.0, 1.0, 0 }, new[] { System.Math.Sqrt(0.5), System.Math.Sqrt(0.5), 0.0 }));
        }

        [Fact]
        public void A_wye_leaning_the_other_way_along_the_run_is_refused()
        {
            var r = ObliquePipeTeeRules.SolveRollAndSlide(new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 0 }, new[] { -0.2, 0.2, 0 },
                new[] { -System.Math.Sqrt(0.5), System.Math.Sqrt(0.5), 0.0 }, new[] { 0.0, 0, 0 }, new[] { System.Math.Sqrt(0.5), System.Math.Sqrt(0.5), 0.0 });
            Assert.NotNull(r.Problem);
        }
            [Fact]
        public void Swap_is_chosen_only_when_the_lean_is_the_mirror_of_the_drawn_angle()
        {
            double h = System.Math.Sqrt(0.5);
            var run = new[] { 1.0, 0, 0 };
            var drawn = new[] { h, h, 0.0 };                       // 45 deg to the run
            Assert.True(ObliquePipeTeeRules.LeanIsOpposite(run, new[] { -h, h, 0.0 }, drawn));   // 135 deg: swap
            Assert.True(ObliquePipeTeeRules.LeanIsOpposite(run, new[] { -h, 0.0, h }, drawn));   // 135 deg, rolled: still swap
            Assert.False(ObliquePipeTeeRules.LeanIsOpposite(run, new[] { h, 0.0, h }, drawn));   // 45 deg: keep order
            Assert.False(ObliquePipeTeeRules.LeanIsOpposite(run, new[] { 0.0, 1.0, 0.0 }, drawn)); // 90 deg: neither, not a swap
        }

        [Fact]
        public void Swap_is_chosen_for_a_drawn_branch_leaning_against_the_run_direction()
        {
            double h = System.Math.Sqrt(0.5);
            var run = new[] { 1.0, 0, 0 };
            var drawn = new[] { -h, h, 0.0 };                      // 135 deg to the run
            Assert.True(ObliquePipeTeeRules.LeanIsOpposite(run, new[] { h, h, 0.0 }, drawn));
            Assert.False(ObliquePipeTeeRules.LeanIsOpposite(run, new[] { -h, h, 0.0 }, drawn));
        }

        [Fact]
        public void Flip_axis_is_the_branch_perpendicular_to_the_run_and_the_half_turn_mirrors_the_lean()
        {
            double h = System.Math.Sqrt(0.5);
            var run = new[] { 1.0, 0, 0 };
            var port = new[] { -h, h, 0.0 };                       // 135 deg to the run
            var axis = ObliquePipeTeeRules.FlipAxis(run, port);
            Assert.Equal(0.0, axis[0], 9); Assert.Equal(1.0, axis[1], 9); Assert.Equal(0.0, axis[2], 9);
            var after = ObliquePipeTeeRules.FlipDirection(port, axis);
            Assert.Equal(h, after[0], 9); Assert.Equal(h, after[1], 9);   // 45 deg, same side
            var u = ObliquePipeTeeRules.FlipDirection(run, axis);
            Assert.Equal(-1.0, u[0], 9);                                  // the run reverses
            Assert.False(ObliquePipeTeeRules.LeanIsOpposite(run, after, new[] { h, h, 0.0 }));
            Assert.True(ObliquePipeTeeRules.FlipLeanMatches(after, new[] { h, h, 0.0 }));
        }

        [Fact]
        public void Flip_keeps_the_azimuth_for_a_rolled_port_and_has_no_axis_along_the_run()
        {
            double h = System.Math.Sqrt(0.5);
            var run = new[] { 0.0, 0, 1.0 };
            var port = new[] { 0.0, h, -h };                       // leans back, azimuth +Y
            var after = ObliquePipeTeeRules.FlipDirection(port, ObliquePipeTeeRules.FlipAxis(run, port));
            Assert.Equal(0.0, after[0], 9); Assert.Equal(h, after[1], 9); Assert.Equal(h, after[2], 9);
            Assert.Null(ObliquePipeTeeRules.FlipAxis(run, new[] { 0.0, 0, -1.0 }));
        }

        [Fact]
        public void Flip_point_mirrors_run_ports_about_the_axis_point_and_stays_on_the_run_line()
        {
            var run = new[] { 1.0, 0, 0 };
            var axisPoint = ObliquePipeTeeRules.ProjectOntoLine(new[] { 2.0, 0.5, 0 }, new[] { 0.0, 0, 0 }, run);
            Assert.Equal(2.0, axisPoint[0], 9); Assert.Equal(0.0, axisPoint[1], 9);
            var axis = new[] { 0.0, 1.0, 0.0 };
            var a = ObliquePipeTeeRules.FlipPoint(new[] { 1.0, 0, 0 }, axisPoint, axis);
            Assert.Equal(3.0, a[0], 9); Assert.Equal(0.0, a[1], 9); Assert.Equal(0.0, a[2], 9);
            Assert.True(ObliquePipeTeeRules.IsCoaxialWithRun(run, new[] { 0.0, 0, 0 }, a, new[] { -1.0, 0, 0 }, 0.003));
        }

        [Fact]
        public void Coaxial_check_rejects_an_offset_or_tilted_port_and_accepts_either_direction()
        {
            var run = new[] { 1.0, 0, 0 };
            var o = new[] { 0.0, 0, 0 };
            Assert.True(ObliquePipeTeeRules.IsCoaxialWithRun(run, o, new[] { 5.0, 0.001, 0 }, new[] { 1.0, 0, 0 }, 0.003));
            Assert.True(ObliquePipeTeeRules.IsCoaxialWithRun(run, o, new[] { 5.0, 0, 0 }, new[] { -1.0, 0, 0 }, 0.003));
            Assert.False(ObliquePipeTeeRules.IsCoaxialWithRun(run, o, new[] { 5.0, 0.01, 0 }, new[] { 1.0, 0, 0 }, 0.003));
            Assert.False(ObliquePipeTeeRules.IsCoaxialWithRun(run, o, new[] { 5.0, 0, 0 }, new[] { 1.0, 0.2, 0 }, 0.003));
        }

        [Fact]
        public void Ports_oppose_only_when_anti_parallel()
        {
            Assert.True(ObliquePipeTeeRules.PortsOppose(new[] { 1.0, 0, 0 }, new[] { -1.0, 0, 0 }));
            Assert.False(ObliquePipeTeeRules.PortsOppose(new[] { 1.0, 0, 0 }, new[] { 1.0, 0, 0 }));
            Assert.False(ObliquePipeTeeRules.PortsOppose(new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }));
        }

        [Fact]
        public void Flip_is_refused_for_a_reducing_wye_and_allowed_for_a_symmetric_one()
        {
            double tol = 0.5 / 304.8, r50 = 25.4 / 304.8, r76 = 38.1 / 304.8;
            Assert.Null(ObliquePipeTeeRules.FlipBoreProblem(r50, r50, r50, r50, tol));
            Assert.NotNull(ObliquePipeTeeRules.FlipBoreProblem(r76, r50, r76, r50, tol)); // the large port would meet the small pipe
            Assert.NotNull(ObliquePipeTeeRules.FlipBoreProblem(r50, r76, r50, r76, tol));
        }
    }
}
