// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// MEASURED, Revit 2024 (anonymized regression case, etapa 1c): the audit reported overlapping_bar at -2.335 mm between a beam's U leg
// and a column hoop's 135 degree hook, where the plan - whose centrelines carry no hooks - had found nothing. A hook
// is not measured by design: the audit cuts it off the drawn bar "where the hook-free centreline ends". It cut at the
// drawn vertex NEAREST the hook-free end, and a hooked bar's hook-free end is its out-to-out corner, which lies off the
// drawn bar: the nearest drawn vertex was half way round the hook's bend, so half the bend was measured. An open bar is
// now cut where its drawn centreline leaves the hook-free one: the bend's first point.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarOverlapHookTrimTests
    {
        // An anonymized sloped top bar with 180 degree hooks at both ends.
        private static readonly List<double[]> Drawn = new List<double[]>
        {
            new[] { 0.000, 98.307, 57.909 }, new[] { 0.000, 131.786, 59.957 }, new[] { 0.000, 148.109, 65.418 },
            new[] { 0.000, 159.513, 78.309 }, new[] { 0.000, 162.945, 95.175 }, new[] { 0.000, 157.483, 111.497 },
            new[] { 0.000, 144.593, 122.902 }, new[] { 0.000, 127.727, 126.333 }, new[] { 0.000, -7377.406, -332.701 },
            new[] { 0.000, -7378.421, -332.747 }, new[] { 0.000, -7379.436, -332.763 }, new[] { 0.000, -8250.576, -332.763 },
            new[] { 0.000, -8267.201, -337.217 }, new[] { 0.000, -8279.371, -349.388 }, new[] { 0.000, -8283.826, -366.013 },
            new[] { 0.000, -8279.371, -382.638 }, new[] { 0.000, -8267.201, -394.808 }, new[] { 0.000, -8250.576, -399.263 },
            new[] { 0.000, -8217.034, -399.263 }
        };
        // Its hook-free centreline as Revit reports it (hooks and bend radii suppressed): the out-to-out corners.
        private static readonly List<double[]> AsDeclared = new List<double[]>
        {
            new[] { 0.000, 165.656, 128.653 }, new[] { 0.000, -7378.420, -332.763 }, new[] { 0.000, -8288.576, -332.763 }
        };

        [Fact]
        public void A_hooked_open_bar_is_cut_where_the_drawn_bar_leaves_its_hook_free_line()
        {
            List<double[]> body = RebarOverlap.Trim(Drawn, AsDeclared);
            Assert.Equal(new[] { 0.000, 127.727, 126.333 }, body[0]);
            Assert.Equal(new[] { 0.000, -8250.576, -332.763 }, body[body.Count - 1]);
        }

        [Fact]
        public void An_unhooked_open_bar_is_not_cut()
        {
            var drawn = new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 1000, 0, 0 } };
            List<double[]> body = RebarOverlap.Trim(drawn, drawn);
            Assert.Equal(2, body.Count);
        }
    }
}
