using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class HookSideTests
    {
        // MEASURED, Revit 2024 (anonymized regression case roof, Paso 3 etapa 1b): a straight #3 bar with a 180 degree hook at both ends,
        // created from curves with normal +Y and BOTH hooks declared 'left', matched the project's shape M_01, drew both
        // hooks DOWN (the side normal x tangent) - and Rebar.GetHookOrientation read 'right' at both ends. The same bar
        // declared 'right' drew both hooks UP and read 'right'. A bent bar of the same mat (Rebar Shape 1, new) declared
        // 'left' drew its hooks down and read 'left'. So the label alone cannot say which way a hook turns; the drawn
        // centreline can. These are the points Revit drew, read back with GetCenterlineCurves.
        private static readonly List<double[]> TransDrawn = new List<double[]>
        {
            new[] { -203.686, -224.288, 2591.212 }, new[] { -237.228, -224.288, 2591.212 }, new[] { -253.853, -224.288, 2595.667 },
            new[] { -266.023, -224.288, 2607.837 }, new[] { -270.478, -224.288, 2624.462 }, new[] { -266.023, -224.288, 2641.087 },
            new[] { -253.853, -224.288, 2653.258 }, new[] { -237.228, -224.288, 2657.712 }, new[] { 7736.772, -224.288, 2657.712 },
            new[] { 7753.397, -224.288, 2653.258 }, new[] { 7765.568, -224.288, 2641.087 }, new[] { 7770.022, -224.288, 2624.462 },
            new[] { 7765.568, -224.288, 2607.837 }, new[] { 7753.397, -224.288, 2595.667 }, new[] { 7736.772, -224.288, 2591.212 },
            new[] { 7703.23, -224.288, 2591.212 }
        };
        private static readonly List<double[]> TransDeclared = new List<double[]>
        {
            new[] { -275.228, -224.288, 2657.712 }, new[] { 7774.772, -224.288, 2657.712 }
        };

        private static readonly List<double[]> LongDrawn = new List<double[]>
        {
            new[] { -220.465, 8098.307, 3057.909 }, new[] { -220.465, 8131.786, 3059.957 }, new[] { -220.465, 8148.109, 3065.418 },
            new[] { -220.465, 8159.513, 3078.309 }, new[] { -220.465, 8162.945, 3095.175 }, new[] { -220.465, 8157.483, 3111.497 },
            new[] { -220.465, 8144.593, 3122.902 }, new[] { -220.465, 8127.727, 3126.333 }, new[] { -220.465, 622.594, 2667.299 },
            new[] { -220.465, 621.579, 2667.253 }, new[] { -220.465, 620.564, 2667.237 }, new[] { -220.465, -250.576, 2667.237 },
            new[] { -220.465, -267.201, 2662.783 }, new[] { -220.465, -279.371, 2650.612 }, new[] { -220.465, -283.826, 2633.987 },
            new[] { -220.465, -279.371, 2617.362 }, new[] { -220.465, -267.201, 2605.192 }, new[] { -220.465, -250.576, 2600.737 },
            new[] { -220.465, -217.034, 2600.737 }
        };
        private static readonly List<double[]> LongDeclared = new List<double[]>
        {
            new[] { -220.465, 8165.656, 3128.653 }, new[] { -220.465, 621.58, 2667.237 }, new[] { -220.465, -288.576, 2667.237 }
        };

        [Fact]
        public void The_measured_straight_bar_Revit_labels_right_turns_left_at_both_ends()
        {
            double[] n = { 0, 1, 0 };
            Assert.Equal(HookSide.Left, HookSide.Measure(TransDrawn, TransDeclared, n, 0, out _));
            Assert.Equal(HookSide.Left, HookSide.Measure(TransDrawn, TransDeclared, n, 1, out _));
        }

        [Fact]
        public void The_measured_bent_bar_labelled_left_turns_left_at_both_ends()
        {
            double[] n = { 1, 0, 0 };
            Assert.Equal(HookSide.Left, HookSide.Measure(LongDrawn, LongDeclared, n, 0, out _));
            Assert.Equal(HookSide.Left, HookSide.Measure(LongDrawn, LongDeclared, n, 1, out _));
        }

        [Fact]
        public void The_same_hooks_seen_against_the_opposite_normal_turn_right()
        {
            double[] n = { 0, -1, 0 };
            Assert.Equal(HookSide.Right, HookSide.Measure(TransDrawn, TransDeclared, n, 0, out _));
            Assert.Equal(HookSide.Right, HookSide.Measure(TransDrawn, TransDeclared, n, 1, out _));
        }

        [Fact]
        public void Hooks_turned_up_are_the_other_side()
        {
            var up = new List<double[]>();
            foreach (double[] p in TransDrawn) up.Add(new[] { p[0], p[1], 2657.712 + (2657.712 - p[2]) });
            double[] n = { 0, 1, 0 };
            Assert.Equal(HookSide.Right, HookSide.Measure(up, TransDeclared, n, 0, out _));
            Assert.Equal(HookSide.Right, HookSide.Measure(up, TransDeclared, n, 1, out _));
        }

        [Fact]
        public void An_end_with_no_hook_drawn_has_no_side()
        {
            Assert.Null(HookSide.Measure(TransDeclared, TransDeclared, new double[] { 0, 1, 0 }, 0, out string why));
            Assert.Contains("no hook", why);
        }

        [Fact]
        public void A_hook_drawn_along_the_normal_has_no_side_in_the_bar_plane()
        {
            var drawn = new List<double[]> { new double[] { 0, 60, 0 }, new double[] { 0, 0, 0 }, new double[] { 1000, 0, 0 } };
            var decl = new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 1000, 0, 0 } };
            Assert.Null(HookSide.Measure(drawn, decl, new double[] { 0, 1, 0 }, 0, out string why));
            Assert.Contains("plane", why);
        }

        [Fact]
        public void Unreadable_input_is_no_answer_rather_than_a_side()
        {
            Assert.Null(HookSide.Measure(null, TransDeclared, new double[] { 0, 1, 0 }, 0, out _));
            Assert.Null(HookSide.Measure(TransDrawn, null, new double[] { 0, 1, 0 }, 0, out _));
            Assert.Null(HookSide.Measure(TransDrawn, TransDeclared, new double[] { 0, 0, 0 }, 0, out _));
            Assert.Null(HookSide.Measure(TransDrawn, TransDeclared, null, 1, out _));
        }

        [Fact]
        public void A_closed_stirrup_with_135_hooks_inward_turns_toward_its_core()
        {
            // A 150 x 300 stirrup in the plane x = 0 (normal +X), starting at its top-left corner and running +Y along
            // the top. A 135 degree hook at the start goes down and back into the core: the side normal x tangent =
            // X x Y = +Z is UP, so a hook into the core (down) is 'right'.
            var decl = new List<double[]>
            {
                new double[] { 0, 0, 300 }, new double[] { 0, 150, 300 }, new double[] { 0, 150, 0 }, new double[] { 0, 0, 0 }, new double[] { 0, 0, 300 }
            };
            var drawn = new List<double[]> { new double[] { 0, 50, 230 } };
            drawn.AddRange(decl);
            drawn.Add(new double[] { 0, 70, 250 });
            Assert.Equal(HookSide.Right, HookSide.Measure(drawn, decl, new double[] { 1, 0, 0 }, 0, out _));
        }
    }
}
